// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamPoweredPlayer.Core.Playback;

/// <summary>Construction options of <see cref="MpvPlayer"/>.</summary>
public sealed record MpvPlayerOptions
{
    public const long DefaultDemuxerMaxBytes = 64L * 1024 * 1024;
    public const long DefaultDemuxerMaxBackBytes = 32L * 1024 * 1024;

    public MpvOutput Output { get; init; } = MpvOutput.Render;
    public DecodeMode Decode { get; init; } = DecodeMode.Gpu;

    /// <summary>
    /// Read-ahead ceiling of mpv's demuxer cache. mpv's default is 150 MiB forward + 50 MiB back; spike S1 saw RSS grow about 19 MB/min
    /// until it reached that 200 MiB. At Steam's ~22 Mbit/s (2.75 MB/s) 64 MiB is ~23 s of read-ahead, more than seven 3 s chunks, which
    /// is plenty for local files and for the download-first cache; and 32 MiB behind the playhead (~11 s) keeps a short rewind cheap.
    /// Together about 96 MiB instead of 200 MiB, i.e. roughly 100 MB less RSS at steady state.
    /// </summary>
    public long DemuxerMaxBytes { get; init; } = DefaultDemuxerMaxBytes;

    /// <summary>See <see cref="DemuxerMaxBytes"/>.</summary>
    public long DemuxerMaxBackBytes { get; init; } = DefaultDemuxerMaxBackBytes;

    /// <summary>Initial volume in percent (mpv's 0..100 scale).</summary>
    public double InitialVolume { get; init; } = 100;

    /// <summary>Minimum mpv log level delivered to <see cref="MpvPlayer.LogMessage"/> ("no" disables it).</summary>
    public string LogLevel { get; init; } = "warn";

    /// <summary>Extra <c>name=value</c> mpv options, applied last (tests, diagnostics).</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}

/// <summary>
/// One libmpv instance. Thread-safe. Events are drained on a dedicated thread that a wakeup callback signals (no polling) and are
/// raised on that thread. Dispose the renderer before the player (the render context must be freed before the mpv handle).
/// </summary>
public sealed class MpvPlayer : IMpvPlayer
{
    public static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromSeconds(10);

    private const ulong ReplyTimePos = 1, ReplyPause = 2, ReplyEof = 3, ReplySpeed = 4, ReplyVolume = 5, ReplyMute = 6, ReplyAudioDelay = 7;

    private readonly nint _handle;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ReaderWriterLockSlim _life = new(LockRecursionPolicy.SupportsRecursion);
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<int>> _commands = new();
    private readonly object _loadGate = new();
    private GCHandle _self;
    private LoadWait? _load;
    private ulong _nextReply = 1000;
    private volatile bool _disposed, _stopThread;
    private int _disposeState;

    private double _timePos = double.NaN, _speed = 1, _volume = 100, _audioDelay;
    private volatile bool _paused = true, _eof, _muted;

    /// <summary>mpv_handle, for <see cref="MpvD3D11Renderer"/>.</summary>
    internal nint NativeHandle => _handle;

    public MpvPlayerOptions Options { get; }

    public double TimePos => Volatile.Read(ref _timePos);
    public bool Paused => _paused;
    public bool EofReached => _eof;
    public double Speed => Volatile.Read(ref _speed);
    public double Volume => Volatile.Read(ref _volume);
    public bool Muted => _muted;
    public double AudioDelay => Volatile.Read(ref _audioDelay);

    public event Action<double>? TimePosChanged;
    public event Action<bool>? PauseChanged;
    public event Action<bool>? EofReachedChanged;
    public event Action<double>? SpeedChanged;
    public event Action<double>? VolumeChanged;
    public event Action<bool>? MuteChanged;
    public event Action<double>? AudioDelayChanged;
    public event Action? PlaybackRestart;
    public event Action? FileLoaded;
    public event Action<MpvEndFile>? EndFile;
    public event Action<MpvLogMessage>? LogMessage;

    public unsafe MpvPlayer(MpvPlayerOptions? options = null)
    {
        Options = options ?? new MpvPlayerOptions();
        if (!MpvLibrary.IsAvailable())
            throw new MpvException($"{MpvLibrary.LibraryName}.dll was not found (application directory, {MpvLibrary.EnvironmentVariable}, MpvLibrary.SearchDirectory).");

        _handle = MpvNative.mpv_create();
        if (_handle == 0) throw new MpvException("mpv_create failed");
        try
        {
            ApplyOptions();
            Check(MpvNative.mpv_initialize(_handle), "mpv_initialize");
            _self = GCHandle.Alloc(this);
            MpvNative.mpv_set_wakeup_callback(_handle, &OnWakeup, GCHandle.ToIntPtr(_self));
            if (Options.LogLevel != "no") MpvNative.mpv_request_log_messages(_handle, Options.LogLevel);
            Observe(ReplyTimePos, "time-pos", MpvConst.FormatDouble);
            Observe(ReplyPause, "pause", MpvConst.FormatFlag);
            Observe(ReplyEof, "eof-reached", MpvConst.FormatFlag);
            Observe(ReplySpeed, "speed", MpvConst.FormatDouble);
            Observe(ReplyVolume, "volume", MpvConst.FormatDouble);
            Observe(ReplyMute, "mute", MpvConst.FormatFlag);
            Observe(ReplyAudioDelay, "audio-delay", MpvConst.FormatDouble);
        }
        catch
        {
            if (_self.IsAllocated) _self.Free();
            MpvNative.mpv_terminate_destroy(_handle);
            throw;
        }
        _thread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _thread.Start();
        _wake.Set();      // drain whatever queued before the callback was set
    }

    private void ApplyOptions()
    {
        var o = Options;
        var inv = CultureInfo.InvariantCulture;
        Opt("config", "no");                       // no user mpv.conf: behaviour must not depend on the machine
        Opt("idle", "yes");
        Opt("keep-open", "yes");                   // EOF holds the last frame (and sets pause)
        Opt("hr-seek", "yes");                     // frame-accurate start= (plan §3 item 3)
        Opt("osc", "no");
        Opt("osd-level", "0");
        Opt("input-default-bindings", "no");
        Opt("input-vo-keyboard", "no");
        Opt("terminal", "no");
        Opt("load-scripts", "no");
        Opt("ytdl", "no");
        Opt("audio-pitch-correction", "yes");      // speed 0.25-4x keeps the pitch
        Opt("pause", "yes");
        Opt("volume", o.InitialVolume.ToString("R", inv));
        Opt("hwdec", o.Decode == DecodeMode.Gpu ? "d3d11va" : "no");
        Opt("demuxer-max-bytes", o.DemuxerMaxBytes.ToString(inv));
        Opt("demuxer-max-back-bytes", o.DemuxerMaxBackBytes.ToString(inv));
        if (o.Output == MpvOutput.Render) Opt("vo", "libmpv");
        else { Opt("vo", "null"); Opt("ao", "null"); Opt("audio-display", "no"); }
        if (o.Extra is not null)
            foreach (var (k, v) in o.Extra) Opt(k, v);
    }

    private void Opt(string name, string value) =>
        Check(MpvNative.mpv_set_option_string(_handle, name, value), $"option {name}={value}");

    private void Observe(ulong reply, string name, int format) =>
        Check(MpvNative.mpv_observe_property(_handle, reply, name, format), $"observe {name}");

    private static void Check(int rc, string what)
    {
        if (rc < 0) throw new MpvException($"{what}: {MpvNative.ErrorString(rc)}", rc);
    }

    // ---- commands ------------------------------------------------------------------------------------------------

    /// <summary>Runs an mpv command synchronously; returns mpv's error code (negative on failure).</summary>
    public unsafe int Command(params string[] args)
    {
        var argv = AllocArgs(args);
        try
        {
            _life.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                return MpvNative.mpv_command(_handle, argv);
            }
            finally { _life.ExitReadLock(); }
        }
        finally { FreeArgs(argv, args.Length); }
    }

    /// <summary>Queues an mpv command; completes with mpv's error code when mpv has run it.</summary>
    public unsafe Task<int> CommandAsync(params string[] args)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _nextReply);
        _commands[id] = tcs;
        var argv = AllocArgs(args);
        try
        {
            _life.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                var rc = MpvNative.mpv_command_async(_handle, id, argv);
                if (rc < 0 && _commands.TryRemove(id, out _)) tcs.TrySetResult(rc);
            }
            finally { _life.ExitReadLock(); }
        }
        catch
        {
            _commands.TryRemove(id, out _);
            throw;
        }
        finally { FreeArgs(argv, args.Length); }      // mpv copies the strings
        return tcs.Task;
    }

    private static unsafe nint* AllocArgs(string[] args)
    {
        var p = (nint*)NativeMemory.AllocZeroed((nuint)((args.Length + 1) * sizeof(nint)));
        for (var i = 0; i < args.Length; i++) p[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        return p;
    }

    private static unsafe void FreeArgs(nint* p, int count)
    {
        for (var i = 0; i < count; i++) Marshal.FreeCoTaskMem(p[i]);
        NativeMemory.Free(p);
    }

    // ---- properties ----------------------------------------------------------------------------------------------

    public unsafe string? GetString(string name)
    {
        _life.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            var p = MpvNative.mpv_get_property_string(_handle, name);
            if (p == 0) return null;
            try { return Marshal.PtrToStringUTF8(p); } finally { MpvNative.mpv_free(p); }
        }
        finally { _life.ExitReadLock(); }
    }

    public unsafe double GetDouble(string name)
    {
        _life.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            var d = double.NaN;
            return MpvNative.mpv_get_property(_handle, name, MpvConst.FormatDouble, &d) < 0 ? double.NaN : d;
        }
        finally { _life.ExitReadLock(); }
    }

    public unsafe long? GetInt64(string name)
    {
        _life.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            long v = 0;
            return MpvNative.mpv_get_property(_handle, name, MpvConst.FormatInt64, &v) < 0 ? null : v;
        }
        finally { _life.ExitReadLock(); }
    }

    public unsafe bool? GetFlag(string name)
    {
        _life.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            var v = 0;
            return MpvNative.mpv_get_property(_handle, name, MpvConst.FormatFlag, &v) < 0 ? null : v != 0;
        }
        finally { _life.ExitReadLock(); }
    }

    /// <summary>Sets a property from a string (mpv parses it; numbers must already be invariant-culture text).</summary>
    public unsafe void SetString(string name, string value)
    {
        _life.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            Check(MpvNative.mpv_set_property_string(_handle, name, value), $"set {name}={value}");
        }
        finally { _life.ExitReadLock(); }
    }

    public unsafe void SetDouble(string name, double value)
    {
        _life.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            Check(MpvNative.mpv_set_property(_handle, name, MpvConst.FormatDouble, &value), $"set {name}");
        }
        finally { _life.ExitReadLock(); }
    }

    public unsafe void SetFlag(string name, bool value)
    {
        _life.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            var v = value ? 1 : 0;
            Check(MpvNative.mpv_set_property(_handle, name, MpvConst.FormatFlag, &v), $"set {name}");
        }
        finally { _life.ExitReadLock(); }
    }

    // ---- API -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plan §3 item 0: opens mpv once on a tiny EDL (2 chunks) at app start so the first real open is fast (S2: 302 ms fresh,
    /// 95 ms pre-warmed). Loads paused; the next <see cref="LoadAsync"/> replaces it.
    /// </summary>
    public Task<bool> PreWarmAsync(string tinyEdl, double startSeconds = 0, CancellationToken cancellationToken = default) =>
        LoadAsync(tinyEdl, startSeconds, play: false, cancellationToken);

    public Task<bool> LoadAsync(string edl, double startSeconds, bool play, CancellationToken cancellationToken = default) =>
        LoadAsync(edl, startSeconds, play, DefaultLoadTimeout, cancellationToken);

    /// <inheritdoc cref="IMpvPlayer.LoadAsync"/>
    public async Task<bool> LoadAsync(string edl, double startSeconds, bool play, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(edl);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var wait = new LoadWait();
        LoadWait? previous;
        lock (_loadGate) { previous = _load; _load = wait; }
        previous?.Tcs.TrySetResult(false);              // superseded

        try
        {
            SetFlag("pause", !play);                   // `pause` survives loadfile (S1): always explicit
            var start = "start=" + startSeconds.ToString("R", CultureInfo.InvariantCulture);
            var rc = await CommandAsync("loadfile", EdlSource(edl), "replace", "-1", start).ConfigureAwait(false);
            if (rc < 0) throw new MpvException($"loadfile: {MpvNative.ErrorString(rc)}", rc);
            return await wait.Tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (_loadGate) { if (ReferenceEquals(_load, wait)) _load = null; }
        }
    }

    /// <summary>What <c>loadfile</c> opens for an EDL text: mpv reads an in-memory stream, so no temp file is written.</summary>
    private static string EdlSource(string edl) => "memory://" + edl;

    public void SetPause(bool paused) => SetFlag("pause", paused);

    /// <summary>0.25-4x with pitch correction (<c>audio-pitch-correction=yes</c>); out-of-range values are clamped.</summary>
    public void SetSpeed(double speed) => SetDouble("speed", Math.Clamp(double.IsNaN(speed) ? 1 : speed, 0.25, 4.0));

    public void SetVolume(double percent) => SetDouble("volume", Math.Clamp(double.IsNaN(percent) ? 100 : percent, 0, 130));

    public void SetMute(bool muted) => SetFlag("mute", muted);

    /// <summary>Audio delay in seconds (positive = audio later), live.</summary>
    public void SetAudioDelay(double seconds) => SetDouble("audio-delay", seconds);

    public void FrameStep()
    {
        var rc = Command("frame-step");
        if (rc < 0) throw new MpvException($"frame-step: {MpvNative.ErrorString(rc)}", rc);
    }

    public void SetAbLoop(double? a, double? b)
    {
        if (a is { } x) SetDouble("ab-loop-a", x); else SetString("ab-loop-a", "no");
        if (b is { } y) SetDouble("ab-loop-b", y); else SetString("ab-loop-b", "no");
    }

    public async Task ScreenshotToFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var rc = await CommandAsync("screenshot-to-file", path, "video").WaitAsync(cancellationToken).ConfigureAwait(false);
        if (rc < 0) throw new MpvException($"screenshot-to-file: {MpvNative.ErrorString(rc)}", rc);
    }

    // ---- event loop ----------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnWakeup(nint ctx)
    {
        // Called by mpv on an arbitrary thread: no mpv calls here, just signal.
        if (GCHandle.FromIntPtr(ctx).Target is MpvPlayer p) p._wake.Set();
    }

    private unsafe void EventLoop()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_stopThread) return;
            _life.EnterReadLock();
            try
            {
                if (_disposed) return;
                while (!_stopThread)
                {
                    var ev = MpvNative.mpv_wait_event(_handle, 0);
                    if (ev == null || ev->EventId == MpvConst.EvNone) break;
                    if (ev->EventId == MpvConst.EvShutdown) return;
                    try { Handle(ev); }
                    catch (Exception) { /* a subscriber threw: it must not kill the event loop */ }
                }
            }
            finally { _life.ExitReadLock(); }
        }
    }

    private unsafe void Handle(MpvEventRaw* ev)
    {
        switch (ev->EventId)
        {
            case MpvConst.EvPropertyChange:
                OnProperty(ev->ReplyUserdata, (MpvEventProperty*)ev->Data);
                break;
            case MpvConst.EvStartFile:
                lock (_loadGate) { if (_load is { } l) l.Started = true; }
                break;
            case MpvConst.EvFileLoaded:
                Raise(FileLoaded);
                break;
            case MpvConst.EvPlaybackRestart:
                Raise(PlaybackRestart);
                LoadWait? done = null;
                lock (_loadGate) { if (_load is { Started: true } l) done = l; }
                done?.Tcs.TrySetResult(true);
                break;
            case MpvConst.EvEndFile:
                OnEndFile((MpvEventEndFile*)ev->Data);
                break;
            case MpvConst.EvCommandReply:
                if (_commands.TryRemove(ev->ReplyUserdata, out var tcs)) tcs.TrySetResult(ev->Error);
                break;
            case MpvConst.EvLogMessage:
                OnLog((MpvEventLogMessage*)ev->Data);
                break;
        }
    }

    private unsafe void OnEndFile(MpvEventEndFile* e)
    {
        var reason = e->Reason switch
        {
            MpvConst.EndEof => MpvEndReason.Eof,
            MpvConst.EndStop => MpvEndReason.Stop,
            MpvConst.EndQuit => MpvEndReason.Quit,
            MpvConst.EndError => MpvEndReason.Error,
            _ => MpvEndReason.Redirect,
        };
        var info = new MpvEndFile(reason, e->Error, reason == MpvEndReason.Error ? MpvNative.ErrorString(e->Error) : null);

        LoadWait? wait = null;
        lock (_loadGate) { if (_load is { Started: true } l) wait = l; }
        if (wait is not null && reason != MpvEndReason.Eof)
        {
            // The load we are waiting for ended before its first frame: it failed (Error) or a newer command stopped it.
            if (reason == MpvEndReason.Error) wait.Tcs.TrySetException(new MpvException($"cannot play the EDL: {info.ErrorText}", e->Error));
            else wait.Tcs.TrySetResult(false);
        }
        Raise(EndFile, info);
    }

    private unsafe void OnLog(MpvEventLogMessage* m)
    {
        if (LogMessage is not { } handler) return;
        var text = Marshal.PtrToStringUTF8(m->Text) ?? "";
        handler(new MpvLogMessage(Marshal.PtrToStringUTF8(m->Prefix) ?? "", Marshal.PtrToStringUTF8(m->Level) ?? "", text.TrimEnd('\r', '\n')));
    }

    private unsafe void OnProperty(ulong reply, MpvEventProperty* p)
    {
        var has = p->Format != MpvConst.FormatNone && p->Data != 0;
        switch (reply)
        {
            case ReplyTimePos:
            {
                var v = has && p->Format == MpvConst.FormatDouble ? *(double*)p->Data : double.NaN;
                Volatile.Write(ref _timePos, v);
                Raise(TimePosChanged, v);
                break;
            }
            case ReplyPause when has:
                _paused = *(int*)p->Data != 0;
                Raise(PauseChanged, _paused);
                break;
            case ReplyEof:
                _eof = has && *(int*)p->Data != 0;
                Raise(EofReachedChanged, _eof);
                break;
            case ReplySpeed when has:
                Volatile.Write(ref _speed, *(double*)p->Data);
                Raise(SpeedChanged, Speed);
                break;
            case ReplyVolume when has:
                Volatile.Write(ref _volume, *(double*)p->Data);
                Raise(VolumeChanged, Volume);
                break;
            case ReplyMute when has:
                _muted = *(int*)p->Data != 0;
                Raise(MuteChanged, _muted);
                break;
            case ReplyAudioDelay when has:
                Volatile.Write(ref _audioDelay, *(double*)p->Data);
                Raise(AudioDelayChanged, AudioDelay);
                break;
        }
    }

    private static void Raise(Action? handler)
    {
        try { handler?.Invoke(); } catch (Exception) { /* subscriber bug: never kill the event thread */ }
    }

    private static void Raise<T>(Action<T>? handler, T value)
    {
        try { handler?.Invoke(value); } catch (Exception) { /* subscriber bug: never kill the event thread */ }
    }

    // ---- lifetime ------------------------------------------------------------------------------------------------

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return ValueTask.CompletedTask;
        return new ValueTask(Task.Run(DisposeCore));
    }

    private unsafe void DisposeCore()
    {
        _disposed = true;
        _stopThread = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread) _thread.Join();

        lock (_loadGate) { _load?.Tcs.TrySetResult(false); _load = null; }
        foreach (var kv in _commands)
            if (_commands.TryRemove(kv.Key, out var tcs)) tcs.TrySetCanceled();

        _life.EnterWriteLock();
        try
        {
            MpvNative.mpv_set_wakeup_callback(_handle, null, 0);
            MpvNative.mpv_terminate_destroy(_handle);      // blocks until mpv's core has shut down
        }
        finally { _life.ExitWriteLock(); }
        if (_self.IsAllocated) _self.Free();
        _wake.Dispose();
        _life.Dispose();
    }

    private sealed class LoadWait
    {
        public readonly TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started;
    }
}
