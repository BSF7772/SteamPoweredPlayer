// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S2 follow-up (s2d): shared helpers for mkcopy / copycheck / deltest.
// This code never removes or moves files. The only way a file can disappear is the EXTERNAL script the maintainer passes via --delete-cmd.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace S2Tool;

static class Dx
{
    /// <summary>Read | Write | Delete sharing: opens that never block Steam's cleanup (same as the product rule).</summary>
    public const FileShare AllShare = (FileShare)7;
    public static readonly JsonSerializerOptions J = new() { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public const string MarkerName = "S2D-COPY.txt";

    public static string Fs(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    public static double R(double v, int d = 3) => Math.Round(v, d);
    public static string Slash(string p) => Path.GetFullPath(p).Replace('\\', '/');
    public static string ChunkPath(string dir, int stream, int n) => Path.Combine(dir, $"chunk-stream{stream}-{n:00000}.m4s");
    public static double BaseOf(SessionMedia s) => 3.0 * (s.Runs[0].First - 1);
    public static int LastChunk(SessionMedia s) => s.Runs[^1].First + s.Runs[^1].Count - 1;
    public static List<int> AllChunks(SessionMedia s) => s.Runs.SelectMany(r => Enumerable.Range(r.First, r.Count)).ToList();
    public static long FreeBytes(string dir) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;

    public static string WriteLeadEdl(SessionMedia s, string path)
    {
        var p = Slash(path);
        File.WriteAllText(p, Edl.Build(s, null, lead: true), new UTF8Encoding(false));
        return p;
    }

    public static IEnumerable<(int a, int b)> ParseRanges(string? s) => s is null ? [] :
        s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => { var p = x.Split('-'); return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[^1], CultureInfo.InvariantCulture)); });

    /// <summary>Open for reading with full sharing. "ok", or the exception type and Win32 code (2 = not found, 5 = access denied = delete pending, 32 = sharing violation).</summary>
    public static string TryOpen(string path)
    {
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, AllShare); return "ok"; }
        catch (Exception e) { return $"{e.GetType().Name} win32={e.HResult & 0xFFFF}"; }
    }

    public static bool Listed(string path)
    {
        try { return Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path)).Any(); } catch { return false; }
    }

    /// <summary>State of a file that was supposed to be removed: gone | delete-pending (listed, cannot be opened) | still-there.</summary>
    public static Dictionary<string, object?> FileState(string path)
    {
        var open = TryOpen(path);
        var listed = Listed(path);
        var status = open == "ok" ? "still-there" : listed ? "listed-but-not-openable (delete-pending)" : open.Contains("win32=2") || open.Contains("win32=3") ? "gone" : "unopenable-not-listed: " + open;
        return new() { ["status"] = status, ["listed"] = listed, ["exists"] = File.Exists(path), ["open"] = open };
    }

    public static string Clip(string? s, int n = 300) => s is null ? "" : s.Length <= n ? s : s[..n] + "...";

    /// <summary>Run the maintainer's external remover script (powershell -File script path). Dry run when script is null. Never kills a hung script.</summary>
    public static async Task<Dictionary<string, object?>> InvokeRemover(string? script, string path)
    {
        if (script is null)
        {
            Console.WriteLine($"WOULD DELETE {path}");
            return new() { ["dryRun"] = true, ["stdout"] = "WOULD DELETE " + path, ["exitCode"] = null };
        }
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, path }) psi.ArgumentList.Add(x);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        bool timedOut = false;
        try { await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token); } catch (OperationCanceledException) { timedOut = true; }
        var r = new Dictionary<string, object?>
        {
            ["dryRun"] = false, ["exitCode"] = timedOut ? null : p.ExitCode, ["timedOut"] = timedOut, ["ms"] = R(sw.Elapsed.TotalMilliseconds, 1),
        };
        if (!timedOut) { r["stdout"] = Clip(await so, 2000); r["stderr"] = Clip(await se, 2000); }
        return r;
    }

    /// <summary>Incremental reader of mpv's log file (shared read).</summary>
    public sealed class LogReader(string path)
    {
        long _pos;
        public int TotalErr, TotalWarn;
        public List<string> Poll()
        {
            var lines = new List<string>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, AllShare);
                if (fs.Length < _pos) _pos = 0;
                var buf = new byte[fs.Length - _pos];
                fs.Seek(_pos, SeekOrigin.Begin); fs.ReadExactly(buf); _pos += buf.Length;
                lines.AddRange(Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (IOException) { }
            return lines;
        }
        public Dictionary<string, object?> Digest(List<string> lines)
        {
            static bool IsE(string l) => l.Contains("[e]");
            static bool IsW(string l) => l.Contains("[w]");
            var hits = lines.Where(l => IsE(l) || IsW(l) || new[] { "error", "fail", "no such file", "cannot", "could not", "sharing", "access is denied", "denied" }.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
            TotalErr += lines.Count(IsE); TotalWarn += lines.Count(IsW);
            return new() { ["newLines"] = lines.Count, ["errors"] = lines.Count(IsE), ["warnings"] = lines.Count(IsW), ["suspect"] = hits.Count, ["sample"] = hits.Take(8).Select(l => Clip(l.TrimEnd('\r'), 220)).ToArray() };
        }
    }

    /// <summary>Background time-pos sampler (wall ms since T0 on the Stopwatch clock).</summary>
    public sealed class Sampler
    {
        public readonly List<(double t, double? tp, bool? idle)> S = [];
        readonly object _l = new();
        Task? _task;
        public void Start(Mpv m, long t0, CancellationToken ct, int periodMs = 250)
        {
            _task = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var tp = await m.GetD("time-pos");
                        var idle = (await m.Get("core-idle")) is { ValueKind: JsonValueKind.True or JsonValueKind.False } e ? e.GetBoolean() : (bool?)null;
                        lock (_l) S.Add((Mpv.Ms(t0, Mpv.Now()), tp, idle));
                        await Task.Delay(periodMs, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { await Task.Delay(periodMs); }
                }
            });
        }
        public Task Stop() => _task ?? Task.CompletedTask;

        /// <summary>Continuity of time-pos in [fromMs, toMs], assuming speed 1 (expected dTp == dWall).</summary>
        public Dictionary<string, object?> Window(double fromMs, double toMs)
        {
            List<(double t, double? tp, bool? idle)> w;
            lock (_l) w = S.Where(x => x.t >= fromMs && x.t <= toMs && x.tp.HasValue).ToList();
            double maxFwd = 0, maxBack = 0, stall = 0, stallStart = double.NaN;
            for (int i = 1; i < w.Count; i++)
            {
                double dTp = w[i].tp!.Value - w[i - 1].tp!.Value, dW = (w[i].t - w[i - 1].t) / 1000.0, ex = dTp - dW;
                maxFwd = Math.Max(maxFwd, ex); maxBack = Math.Min(maxBack, ex);
                if (Math.Abs(dTp) < 0.02) { if (double.IsNaN(stallStart)) stallStart = w[i - 1].t; stall = Math.Max(stall, w[i].t - stallStart); } else stallStart = double.NaN;
            }
            return new()
            {
                ["samples"] = w.Count, ["firstTp"] = w.Count > 0 ? R(w[0].tp!.Value) : null, ["lastTp"] = w.Count > 0 ? R(w[^1].tp!.Value) : null,
                ["maxJumpForwardS"] = R(maxFwd), ["maxJumpBackS"] = R(maxBack), ["longestStallMs"] = R(stall, 0),
            };
        }

        public void WriteCsv(string path)
        {
            lock (_l) File.WriteAllLines(path, new[] { "wall_ms,time_pos,core_idle" }.Concat(S.Select(x => $"{x.t:0},{(x.tp.HasValue ? x.tp.Value.ToString("0.000", CultureInfo.InvariantCulture) : "")},{x.idle}")));
        }
    }

    /// <summary>Playhead chunk number on the lead (tfdt) axis. Chunk n starts at 3(n-1)+0.037 s.</summary>
    public static int ChunkAt(double tp) => (int)Math.Floor((tp - 0.037) / 3.0) + 1;

    public static async Task<double?> AvsyncMeanMs(Mpv m, int n = 10, int gapMs = 100)
    {
        var d = new List<double>();
        for (int i = 0; i < n; i++)
        {
            var a = await m.GetD("audio-pts"); var t = await m.GetD("time-pos");
            if (a is not null && t is not null) d.Add((a.Value - t.Value) * 1000);
            await Task.Delay(gapMs);
        }
        return d.Count > 0 ? R(d.Average(), 1) : null;
    }
}
