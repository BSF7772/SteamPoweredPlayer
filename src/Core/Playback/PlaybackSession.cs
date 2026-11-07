// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Playback;

/// <summary>Why a reload (<c>loadfile &lt;edl&gt; start=&lt;t&gt;</c>) happened.</summary>
public enum ReloadCause
{
    /// <summary>First load of a session.</summary>
    Open,
    /// <summary>The user sought (or stepped back).</summary>
    Seek,
    /// <summary><see cref="EdlWindow.ShouldReload"/> asked for it during sequential play (near the window's end, stale window, periodic).</summary>
    Policy,
    /// <summary><see cref="CacheFollower"/>: Steam trimmed the head or the session grew.</summary>
    CacheChange,
    /// <summary><see cref="CacheFollower"/>: playback reached the end of the window and more media exists.</summary>
    EndOfWindow,
}

/// <summary>A completed reload.</summary>
/// <param name="StartSeconds">Media time loaded at.</param>
/// <param name="WindowRebuilt">False when the loaded EDL was reused (a seek inside the current window).</param>
public sealed record SessionReload(ReloadCause Cause, double StartSeconds, int WindowFirst, int WindowLast, bool WindowRebuilt);

/// <summary>
/// Orchestrates one recording on one <see cref="IMpvPlayer"/>: window EDLs, the seek-by-reload rule, seek coalescing, the periodic
/// reload and the time axes. It does not own the player.
/// </summary>
/// <remarks>
/// Rules (plan §3 items 2-4, S2): every user seek and every back-step is a reload (<c>loadfile start=</c>), never an mpv seek, because a
/// revisited chunk breaks decode and a seek inside the opened chunk can't be told apart from a revisit. A seek that stays inside the
/// loaded window and is not near its end reuses the loaded EDL; otherwise the window is rebuilt around the target. While a reload is in
/// flight new seeks only replace the pending target (latest wins) and all their tasks complete when the last one has loaded.
/// <c>pause</c> is the user's intent kept here, not read from mpv: <c>keep-open</c> pauses mpv at EOF and mpv keeps <c>pause</c> across
/// loads, so every reload passes it explicitly.
/// <para>Time: <see cref="Position"/> is media (tfdt) time, equal to mpv's <c>time-pos</c> with the lead EDL. <see cref="UiPosition"/>
/// subtracts the first chunk's start, so the UI shows 0 at the head. Durations come from the index, never from mpv.</para>
/// </remarks>
public sealed class PlaybackSession : IAsyncDisposable
{
    /// <summary>A seek past the end lands this far before it (so the last frame shows instead of an instant EOF).</summary>
    public const double EndMarginSeconds = 0.05;

    private readonly IMpvPlayer _player;
    private readonly Func<string, string> _url;
    private readonly int _windowCount;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private SessionMedia? _media;
    private IReadOnlyList<int> _window = [];
    private string _edl = "";
    private bool _userPaused;
    private double _position = double.NaN, _lastTick, _playedSinceLoad;
    private bool _reloading, _pumping, _atEnd, _disposed;
    private PendingReload? _pending;
    private readonly List<TaskCompletionSource> _waiters = [];

    private sealed record PendingReload(double Position, ReloadCause Cause, bool Force, bool? Play);

    public PlaybackSession(IMpvPlayer player, Func<string, string>? url = null, int windowCount = EdlWindow.DefaultCount)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _url = url ?? (p => p);
        _windowCount = windowCount;
        _player.TimePosChanged += OnTimePos;
        _player.EofReachedChanged += OnEof;
    }

    /// <summary>The current index (null before <see cref="OpenAsync"/>).</summary>
    public SessionMedia? Media { get { lock (_gate) return _media; } }

    /// <summary>Chunk numbers of the loaded EDL.</summary>
    public IReadOnlyList<int> Window { get { lock (_gate) return _window; } }

    /// <summary>The user wants playback paused (independent of mpv's EOF auto-pause).</summary>
    public bool UserPaused { get { lock (_gate) return _userPaused; } }

    /// <summary>A reload is queued or running.</summary>
    public bool IsReloading { get { lock (_gate) return _reloading; } }

    /// <summary>mpv reported EOF and no reload has happened since.</summary>
    public bool AtEnd { get { lock (_gate) return _atEnd; } }

    /// <summary>Media time in seconds; while a reload is pending it is the (snapped) target.</summary>
    public double Position { get { lock (_gate) return _position; } }

    /// <summary>Media time of the first frame of the first chunk (the UI's zero).</summary>
    public double MediaStartSeconds { get { lock (_gate) return StartOf(_media); } }

    /// <summary>Media time at the end of the last chunk.</summary>
    public double MediaEndSeconds { get { lock (_gate) return EndOf(_media); } }

    /// <summary>0-based UI time: <see cref="Position"/> minus <see cref="MediaStartSeconds"/>.</summary>
    public double UiPosition { get { lock (_gate) return Math.Max(0, _position - StartOf(_media)); } }

    /// <summary>Scrubber length from the index: the span from the first chunk's start to the last chunk's end (gaps included).</summary>
    public double UiDuration { get { lock (_gate) return EndOf(_media) - StartOf(_media); } }

    /// <summary>Playable length from the index (chunks × segment length, gaps excluded).</summary>
    public double PlayableSeconds { get { lock (_gate) return _media?.DurationSeconds ?? 0; } }

    public double MediaToUi(double mediaSeconds) { lock (_gate) return mediaSeconds - StartOf(_media); }
    public double UiToMedia(double uiSeconds) { lock (_gate) return uiSeconds + StartOf(_media); }

    /// <summary>Position moved (sequential play). Not raised while a reload is pending. Raised on the player's thread.</summary>
    public event Action<double>? PositionChanged;

    /// <summary>mpv reached EOF of the loaded window (not raised during a reload). <see cref="CacheFollower"/> decides what happens.</summary>
    public event Action<double>? ReachedEnd;

    public event Action<SessionReload>? Reloaded;

    /// <summary>A reload failed (mpv could not open the EDL, e.g. Steam trimmed a chunk meanwhile). The session stays usable.</summary>
    public event Action<Exception>? ReloadFailed;

    // ---- API -----------------------------------------------------------------------------------------------------

    /// <summary>Loads <paramref name="media"/> at <paramref name="startMediaSeconds"/> (default: its head).</summary>
    public Task OpenAsync(SessionMedia media, double? startMediaSeconds = null, bool play = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        lock (_gate)
        {
            _media = media;
            _window = [];
            _userPaused = !play;
        }
        return Enqueue(new PendingReload(startMediaSeconds ?? StartOf(media), ReloadCause.Open, true, play), cancellationToken);
    }

    /// <summary>Seek to media time (seek-by-reload). Coalesced: only the latest target of a burst is loaded.</summary>
    public Task SeekAsync(double mediaSeconds, CancellationToken cancellationToken = default) =>
        Enqueue(new PendingReload(mediaSeconds, ReloadCause.Seek, false, null), cancellationToken);

    public Task SeekUiAsync(double uiSeconds, CancellationToken cancellationToken = default) => SeekAsync(UiToMedia(uiSeconds), cancellationToken);

    /// <summary>Seek relative to the latest position or pending target.</summary>
    public Task SeekRelativeAsync(double deltaSeconds, CancellationToken cancellationToken = default) => SeekAsync(Position + deltaSeconds, cancellationToken);

    /// <summary>Back-step by one frame (<paramref name="frameSeconds"/>, e.g. 1/60): a reload, since mpv can't step back across a chunk.</summary>
    public Task StepBackAsync(double frameSeconds, CancellationToken cancellationToken = default) => SeekRelativeAsync(-frameSeconds, cancellationToken);

    /// <summary>Forward single-frame step: pauses (user intent too) and lets mpv step.</summary>
    public void FrameStep()
    {
        lock (_gate) _userPaused = true;
        _player.FrameStep();
    }

    public void SetPause(bool paused)
    {
        lock (_gate) _userPaused = paused;
        _player.SetPause(paused);
    }

    public void TogglePause() => SetPause(!UserPaused);

    public void SetSpeed(double speed) => _player.SetSpeed(Math.Clamp(speed, 0.25, 4));
    public void SetVolume(double percent) => _player.SetVolume(percent);
    public void SetMute(bool muted) => _player.SetMute(muted);
    public void SetAudioDelay(double seconds) => _player.SetAudioDelay(seconds);
    public void SetAbLoop(double? a, double? b) => _player.SetAbLoop(a, b);

    /// <summary>Swap in a new index (Steam trimmed or the session grew) without reloading. <see cref="CacheFollower"/> decides what follows.</summary>
    public void ReplaceMedia(SessionMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        lock (_gate) _media = media;
    }

    /// <summary>Reload at a media position, keeping the pause intent. <paramref name="forceRebuild"/> rebuilds the window even if it looks fine.</summary>
    public Task ReloadAsync(double mediaSeconds, ReloadCause cause, bool forceRebuild = true, CancellationToken cancellationToken = default) =>
        Enqueue(new PendingReload(mediaSeconds, cause, forceRebuild, null), cancellationToken);

    /// <summary>
    /// Snaps a target to something playable: before the head → the head, a gap → the start of the next run, past the end → just before the
    /// end. NaN counts as the head.
    /// </summary>
    public static double Snap(SessionMedia media, double seconds)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.ChunkCount == 0) return 0;
        var start = StartOf(media);
        var end = EndOf(media);
        if (double.IsNaN(seconds) || seconds <= start) return start;
        if (seconds >= end) return Math.Max(start, end - EndMarginSeconds);
        var s = media.SegmentSeconds;
        for (var i = 1; i < media.Runs.Count; i++)
        {
            var gapStart = media.Runs[i - 1].Last * s;
            var gapEnd = media.ChunkStartSeconds(media.Runs[i].First);
            if (seconds >= gapStart && seconds < gapEnd) return gapEnd;
        }
        return seconds;
    }

    /// <summary>Pre-warm input (plan §3 item 0): a 2-chunk EDL at the head and the media time to start it at.</summary>
    public static (string Edl, double StartSeconds) BuildPreWarm(SessionMedia media, Func<string, string>? url = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        var tiny = media.Chunks().Take(2).ToList();
        return (Edl.Build(media, tiny, url ?? (p => p)), StartOf(media));
    }

    // ---- reload pump (coalescing) --------------------------------------------------------------------------------

    private Task Enqueue(PendingReload req, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startPump = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_media is null || _media.ChunkCount == 0) throw new InvalidOperationException("No media is open.");
            // Latest wins; a forced rebuild requested earlier is not lost.
            _pending = _pending is { Force: true } && !req.Force ? req with { Force = true } : req;
            _waiters.Add(tcs);
            _reloading = true;
            _position = Snap(_media, req.Position);
            _atEnd = false;
            if (!_pumping) { _pumping = true; startPump = true; }
        }
        if (startPump) _ = Task.Run(PumpAsync);
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            PendingReload req;
            List<TaskCompletionSource> waiters;
            lock (_gate)
            {
                if (_pending is null || _disposed)
                {
                    _pumping = false;
                    _reloading = false;
                    var rest = _waiters.ToList();
                    _waiters.Clear();
                    foreach (var w in rest) w.TrySetCanceled();
                    return;
                }
                req = _pending;
                _pending = null;
                waiters = [.. _waiters];
                _waiters.Clear();
            }

            Exception? error = null;
            try { await DoReloadAsync(req).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { error = e; }
            catch (OperationCanceledException) { error = new OperationCanceledException(); }

            if (error is not null and not OperationCanceledException) SafeInvoke(ReloadFailed, error);
            var finished = false;
            lock (_gate)
            {
                // Leave the reloading state before callers are released, so their next tick or seek is not mistaken for "in flight".
                if (_pending is null) { _pumping = false; _reloading = false; finished = true; }
            }
            foreach (var w in waiters)
            {
                if (error is null) w.TrySetResult();
                else w.TrySetException(error);
            }
            if (finished) return;
        }
    }

    private async Task DoReloadAsync(PendingReload req)
    {
        SessionMedia media;
        bool rebuild;
        double target;
        bool play;
        lock (_gate)
        {
            media = _media!;
            target = Snap(media, req.Position);
            play = req.Play ?? !_userPaused;
            rebuild = req.Force || _window.Count == 0
                      || EdlWindow.ShouldReload(_window, media, target, 0) != ReloadReason.None;
            if (rebuild)
            {
                _window = EdlWindow.Window(media, target, _windowCount);
                _edl = Edl.Build(media, _window, _url);
            }
            _position = target;
        }

        var ok = await _player.LoadAsync(_edl, target, play, _cts.Token).ConfigureAwait(false);
        if (!ok) throw new TimeoutException("mpv did not deliver a first frame for the reload.");

        IReadOnlyList<int> window;
        lock (_gate)
        {
            _lastTick = target;
            _playedSinceLoad = 0;
            _position = target;
            window = _window;
        }
        SafeInvoke(Reloaded, new SessionReload(req.Cause, target, window[0], window[^1], rebuild));
    }

    // ---- mpv events ----------------------------------------------------------------------------------------------

    private void OnTimePos(double t)
    {
        if (double.IsNaN(t)) return;
        ReloadReason reason;
        PendingReload? auto = null;
        lock (_gate)
        {
            if (_reloading || _disposed || _media is null) return;
            var d = t - _lastTick;
            _lastTick = t;
            if (d is > 0 and < 2) _playedSinceLoad += d;      // sequential play; a jump is not "played"
            _position = t;
            reason = EdlWindow.ShouldReload(_window, _media, t, _playedSinceLoad);
            if (reason != ReloadReason.None && Worthwhile(reason, _media, t))
                auto = new PendingReload(t, ReloadCause.Policy, true, null);
        }
        SafeInvoke(PositionChanged, t);
        if (auto is null) return;
        try { _ = Enqueue(auto, default).ContinueWith(static _ => { }, TaskScheduler.Default); }
        catch (ObjectDisposedException) { /* closing */ }
    }

    /// <summary>
    /// NearWindowEnd needs hysteresis: near the live head every new chunk would otherwise trigger a reload every 3 s. Reload only when
    /// the rebuilt window reaches at least <see cref="EdlWindow.ReloadMarginChunks"/> chunks past the loaded one. (Caller holds the lock.)
    /// </summary>
    private bool Worthwhile(ReloadReason reason, SessionMedia media, double t)
    {
        if (reason != ReloadReason.NearWindowEnd) return true;
        var candidate = EdlWindow.Window(media, t, _windowCount);
        return candidate.Count > 0 && _window.Count > 0 && candidate[^1] - _window[^1] >= EdlWindow.ReloadMarginChunks;
    }

    private void OnEof(bool eof)
    {
        double pos;
        lock (_gate)
        {
            if (!eof) return;
            if (_reloading || _disposed) return;
            _atEnd = true;
            pos = double.IsNaN(_player.TimePos) ? _position : _player.TimePos;
            _position = pos;
        }
        SafeInvoke(ReachedEnd, pos);
    }

    private static void SafeInvoke<T>(Action<T>? handler, T arg)
    {
        try { handler?.Invoke(arg); } catch (Exception) { /* subscriber bug */ }
    }

    private static double StartOf(SessionMedia? m) => m is { FirstChunk: { } f } ? m.ChunkStartSeconds(f) : 0;
    private static double EndOf(SessionMedia? m) => m is { LastChunk: { } l } ? l * m.SegmentSeconds : 0;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
        }
        _player.TimePosChanged -= OnTimePos;
        _player.EofReachedChanged -= OnEof;
        _cts.Cancel();
        return ValueTask.CompletedTask;
    }
}
