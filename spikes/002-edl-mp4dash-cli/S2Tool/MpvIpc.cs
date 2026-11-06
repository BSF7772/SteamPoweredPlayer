// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S2: thin mpv JSON-IPC client over a Windows named pipe, with timestamped events.
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace S2Tool;

public sealed record MpvEvent(long Ts, string Name, JsonElement Json);

public sealed class Mpv : IDisposable
{
    readonly Process _proc;
    readonly NamedPipeClientStream _pipe;
    readonly StreamWriter _wr;
    readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly List<MpvEvent> _events = new();
    readonly object _lock = new();
    long _reqId;
    readonly SemaphoreSlim _wrLock = new(1, 1);
    public string? ReadLoopEnd; public int BadLines; public string? LastBadLine;
    public int Pid => _proc.Id;
    public Process Process => _proc;
    public long StartTs { get; }

    public static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
    public static long Now() => Stopwatch.GetTimestamp();

    Mpv(Process p, NamedPipeClientStream pipe, long startTs)
    {
        _proc = p; _pipe = pipe; StartTs = startTs;
        _wr = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(ReadLoop);
    }

    public static string[] ExtraArgs = [];

    public static async Task<Mpv> StartAsync(string exe, string hwdec, string logFile, params string[] extra)
    {
        var name = "spp-s2-" + Guid.NewGuid().ToString("N")[..10];
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        // Same option set the plan's playback path uses, plus a small window so the real VO + hwdec path is exercised.
        foreach (var o in new[]
        {
            "--no-terminal", "--config=no", "--idle=yes", "--keep-open=yes", "--hr-seek=yes", "--osc=no", "--input-default-bindings=no",
            "--no-input-terminal", "--vo=gpu-next", "--gpu-api=d3d11", "--force-window=yes", "--geometry=640x360+40+40", "--title=spp-s2",
            "--ao=wasapi", "--volume=0", "--focus-on=never", "--ontop=no",
            $"--hwdec={hwdec}", $"--log-file={logFile}", "--msg-level=all=v", $"--input-ipc-server=\\\\.\\pipe\\{name}",
        }) psi.ArgumentList.Add(o);
        foreach (var e in extra.Concat(ExtraArgs)) psi.ArgumentList.Add(e);
        var t0 = Now();
        var p = Process.Start(psi)!;
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try { await pipe.ConnectAsync(200); break; }
            catch (TimeoutException) { if (DateTime.UtcNow > deadline || p.HasExited) throw new Exception("mpv IPC pipe never appeared"); }
        }
        return new Mpv(p, pipe, t0);
    }

    async Task ReadLoop()
    {
        using var rd = new StreamReader(_pipe, new UTF8Encoding(false));
        string? line;
        try
        {
            while ((line = await rd.ReadLineAsync()) is not null)
            {
                var ts = Now();
                JsonElement j;
                try { j = JsonDocument.Parse(line).RootElement.Clone(); } catch { BadLines++; LastBadLine = line.Length > 200 ? line[..200] : line; continue; }
                if (j.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.Number)
                {
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_lock) _pending.Remove(rid.GetInt64(), out tcs);
                    tcs?.TrySetResult(j);
                }
                else if (j.TryGetProperty("event", out var ev))
                {
                    var name = ev.GetString()!;
                    if (name == "property-change" && j.TryGetProperty("name", out var pn)) name = "prop:" + pn.GetString();
                    lock (_lock) _events.Add(new MpvEvent(ts, name, j));
                }
            }
        }
        catch (Exception e) { ReadLoopEnd = e.GetType().Name + ": " + e.Message; }
        ReadLoopEnd ??= "eof";
    }

    public async Task<JsonElement> CmdRaw(params object[] args)
    {
        var id = Interlocked.Increment(ref _reqId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _pending[id] = tcs;
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["command"] = args, ["request_id"] = id });
        await _wrLock.WaitAsync();
        try { await _wr.WriteLineAsync(json); } finally { _wrLock.Release(); }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { return await tcs.Task.WaitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            lock (_lock) _pending.Remove(id);
            throw new TimeoutException($"mpv IPC no reply to [{string.Join(' ', args)}] in 30s; readLoopEnd={ReadLoopEnd ?? "running"} badLines={BadLines} last={LastBadLine}");
        }
    }

    public async Task Cmd(params object[] args)
    {
        var r = await CmdRaw(args);
        if (r.GetProperty("error").GetString() != "success") throw new Exception($"mpv cmd [{string.Join(' ', args)}] -> {r.GetProperty("error").GetString()}");
    }

    public async Task<JsonElement?> Get(string prop)
    {
        var r = await CmdRaw("get_property", prop);
        return r.GetProperty("error").GetString() == "success" ? r.GetProperty("data") : null;
    }
    public async Task<double?> GetD(string prop) => (await Get(prop)) is { ValueKind: JsonValueKind.Number } e ? e.GetDouble() : null;
    public async Task<string?> GetS(string prop) => (await Get(prop)) is { } e ? e.ToString() : null;
    public Task Set(string prop, object v) => Cmd("set_property", prop, v);
    public Task Observe(string prop) => Cmd("observe_property", prop.GetHashCode() & 0xffff, prop);

    public long Mark() { lock (_lock) return Now(); }

    /// <summary>Wait for the first event named <paramref name="name"/> with timestamp >= after. Returns its timestamp.</summary>
    public async Task<long> WaitEvent(string name, long after, double timeoutSec = 20, Func<MpvEvent, bool>? pred = null)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < end)
        {
            lock (_lock)
            {
                var e = _events.FirstOrDefault(x => x.Ts >= after && x.Name == name && (pred is null || pred(x)));
                if (e is not null) return e.Ts;
            }
            await Task.Delay(1);
        }
        throw new TimeoutException($"event {name} not seen in {timeoutSec}s");
    }

    public List<MpvEvent> Events(string name, long after = 0) { lock (_lock) return _events.Where(e => e.Name == name && e.Ts >= after).ToList(); }
    public int CountEvents(string name, long after) { lock (_lock) return _events.Count(e => e.Name == name && e.Ts >= after); }

    public void Dispose()
    {
        try { _wr.WriteLine("{\"command\":[\"quit\"]}"); } catch { }
        try { if (!_proc.WaitForExit(3000)) { /* left for the maintainer; we never kill processes */ } } catch { }
        try { _pipe.Dispose(); } catch { }
    }
}
