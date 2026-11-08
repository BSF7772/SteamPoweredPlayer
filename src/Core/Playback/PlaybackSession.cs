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
    /// <summary>The A-B loop reached B and jumped back to A (<see cref="PlaybackSession.SetLoop"/>).</summary>
    Loop,
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

    /// <summary>A seek into the last this-many seconds of a run that is followed by a gap lands this far before the gap (see <see cref="Snap"/>).</summary>
    public const double GapTailMarginSeconds = 0.3;

    private readonly IMpvPlayer _player;
    private readonly Func<string, string> _url;
    private readonly int _windowCount;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private SessionMedia? _media;
    private IReadOnlyList<int> _window = [];
    private string _edl = "";
    private bool _userPaused;
    private double? _loopA, _loopB;
    private bool _loopEngaged;                           // the playhead was put inside A-B: reaching B wraps. A seek outside lets it play on.
    private double _position = double.NaN, _lastTick, _playedSinceLoad;
    private bool _reloading, _pumping, _atEnd, _disposed;
    private PendingReload? _pending;
    private readonly List<TaskCompletionSource> _waiters = [];

    private sealed record PendingReload(double Position, ReloadCause Cause, bool Force, bool? Play, int Back = 0, double Origin = 0, double Frame = 0);

    private List<double>? _frames;                       // consecutive frame pts found by the last probe (a lookup by value, so it never goes stale by seeking)
    private int _probeTimeouts;                          // probes in a row whose frame step reported no time; at the limit, back-steps stay one nominal frame
    private const int ProbeTimeoutLimit = 3;             // one slow decode must not give up exact back-steps for the whole recording
    private const double LoopEdgeSeconds = 0.02;         // a seek this close before A counts as inside the zone

    /// <summary>How long a probe waits for the player to report the frame it stepped to before giving up on exact back-steps.</summary>
    public TimeSpan FrameProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    private const int ProbeMarginFrames = 12;

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
            _frames = null;
            _probeTimeouts = 0;
        }
        try { _player.SetDemuxerCache(DemuxerCache.For(media)); }
        catch (Exception e) when (e is MpvException or ObjectDisposedException) { /* keep the start-up cache size */ }
        return Enqueue(new PendingReload(startMediaSeconds ?? StartOf(media), ReloadCause.Open, true, play), cancellationToken);
    }

    /// <summary>Seek to media time (seek-by-reload). Coalesced: only the latest target of a burst is loaded.</summary>
    public Task SeekAsync(double mediaSeconds, CancellationToken cancellationToken = default) =>
        Enqueue(new PendingReload(mediaSeconds, ReloadCause.Seek, false, null), cancellationToken);

    public Task SeekUiAsync(double uiSeconds, CancellationToken cancellationToken = default) => SeekAsync(UiToMedia(uiSeconds), cancellationToken);

    /// <summary>Seek relative to the latest position or pending target.</summary>
    public Task SeekRelativeAsync(double deltaSeconds, CancellationToken cancellationToken = default) => SeekAsync(Position + deltaSeconds, cancellationToken);

    /// <summary>
    /// Back-step by one frame: a reload, since mpv can't step back across a chunk. Steam's pts are irregular, so "position − 1/fps" misses
    /// frame boundaries (P2): the session finds the previous frame's real pts by stepping forward from a few frames earlier, then loads
    /// between that frame and the next. <paramref name="frameSeconds"/> (e.g. 1/60) is only the estimate used to find where to look, and
    /// the whole step when the player reports no frame times. Rapid calls accumulate: three calls move three frames.
    /// </summary>
    public Task StepBackAsync(double frameSeconds, CancellationToken cancellationToken = default)
    {
        if (!(frameSeconds > 0) || !double.IsFinite(frameSeconds)) return SeekRelativeAsync(-frameSeconds, cancellationToken);
        return Enqueue(new PendingReload(double.NaN, ReloadCause.Seek, false, null, 1, double.NaN, frameSeconds), cancellationToken);
    }

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

    /// <summary>
    /// The A-B loop, driven by the session: when the position reaches B it reloads at A (the seek-by-reload rule). mpv's own ab-loop
    /// seeks inside the EDL, revisits a chunk and then wraps early (P1: every lap after the first was a fraction of A→B), so it stays off.
    /// Both ends in media seconds; null or B ≤ A clears it. A new loop starts active; a seek then decides (<see cref="LoopEngaged"/>).
    /// </summary>
    public void SetLoop(double? a, double? b)
    {
        lock (_gate)
        {
            if (a is { } x && b is { } y && double.IsFinite(x) && double.IsFinite(y) && y > x) { _loopA = x; _loopB = y; _loopEngaged = true; }
            else { _loopA = null; _loopB = null; _loopEngaged = false; }
        }
        _player.SetAbLoop(null, null);
    }

    /// <summary>
    /// The loop is active: a seek landed inside A-B (or it was just set). A seek outside it leaves the marks in place but plays on normally,
    /// on both sides, also through the zone; a seek back into the zone activates it again.
    /// </summary>
    public bool LoopEngaged { get { lock (_gate) return _loopEngaged; } }

    /// <summary>Swap in a new index (Steam trimmed or the session grew) without reloading. <see cref="CacheFollower"/> decides what follows.</summary>
    public void ReplaceMedia(SessionMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        lock (_gate) { _media = media; _frames = null; }
    }

    /// <summary>Reload at a media position, keeping the pause intent. <paramref name="forceRebuild"/> rebuilds the window even if it looks fine.</summary>
    public Task ReloadAsync(double mediaSeconds, ReloadCause cause, bool forceRebuild = true, CancellationToken cancellationToken = default) =>
        Enqueue(new PendingReload(mediaSeconds, cause, forceRebuild, null), cancellationToken);

    /// <summary>
    /// Snaps a target to something playable (the last <see cref="GapTailMarginSeconds"/> before a gap move back that far): before the head → the head, a gap → the start of the next run, past the end → just before the
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
        for (var i = 1; i < media.Runs.Count; i++)
        {
            var gapStart = media.ChunkEndSeconds(media.Runs[i - 1].Last);
            var gapEnd = media.ChunkStartSeconds(media.Runs[i].First);
            // The last frames before a gap are not seekable: the part is stretched over the hole, and an hr-seek into the tail of
            // its content (found by --selftest: from 0.2 s before the content ends) lands in the next run, ~18 s late.
            if (seconds >= gapStart - GapTailMarginSeconds && seconds < gapStart) return gapStart - GapTailMarginSeconds;
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
            if (req.Back > 0)
            {
                // Back-steps accumulate against the origin of the burst; after another kind of pending request they are a nominal seek.
                if (_pending is { Back: > 0 } b) req = req with { Back = b.Back + req.Back, Origin = b.Origin };
                else if (_pending is { } other) req = new PendingReload(other.Position - req.Frame, ReloadCause.Seek, false, null);
                else req = req with { Origin = _position };
                if (req.Back > 0) req = req with { Position = req.Origin - req.Back * req.Frame };
            }
            // Latest wins; a forced rebuild requested earlier is not lost.
            _pending = _pending is { Force: true } && !req.Force ? req with { Force = true } : req;
            _waiters.Add(tcs);
            _reloading = true;
            _position = Snap(_media, req.Position);
            if (req.Cause == ReloadCause.Seek && _loopA is { } la && _loopB is { } lb) _loopEngaged = _position >= la - LoopEdgeSeconds && _position < lb;
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
        double? shown = null;
        if (req.Back > 0)
        {
            var exact = await TryExactBackAsync(req).ConfigureAwait(false);
            if (exact is { } e)
            {
                shown = e.Shown;
                req = req with { Position = e.LoadAt, Play = false };
                lock (_gate) _userPaused = true;                // like the forward step, a frame step is a paused operation
            }
            else req = req with { Position = req.Origin - req.Back * req.Frame };
        }

        double target;
        bool play;
        bool rebuild;
        double? endLimit;
        lock (_gate)
        {
            endLimit = EndLimitOf(_media);
            target = Snap(_media!, req.Position);
            play = req.Play ?? !_userPaused;
            rebuild = EnsureWindow(target, req.Force);
            _position = shown is not null && target == req.Position ? shown.Value : target;
        }

        var ok = await _player.LoadAsync(_edl, target, play, endLimit, _cts.Token).ConfigureAwait(false);
        if (!ok) throw new TimeoutException("mpv did not deliver a first frame for the reload.");

        IReadOnlyList<int> window;
        lock (_gate)
        {
            _lastTick = shown is not null && target == req.Position ? shown.Value : target;
            _playedSinceLoad = 0;
            _position = _lastTick;
            window = _window;
        }
        SafeInvoke(Reloaded, new SessionReload(req.Cause, target, window[0], window[^1], rebuild));
    }

    /// <summary>Rebuilds the window around <paramref name="target"/> when it is forced, empty or no longer suitable. (Caller holds the lock.)</summary>
    private bool EnsureWindow(double target, bool force)
    {
        var media = _media!;
        var rebuild = force || _window.Count == 0 || EdlWindow.ShouldReload(_window, media, target, 0) != ReloadReason.None;
        if (rebuild)
        {
            _window = EdlWindow.Window(media, target, _windowCount);
            _edl = Edl.Build(media, _window, _url);
        }
        return rebuild;
    }

    /// <summary>
    /// The frame <paramref name="req"/>.Back frames before the one on screen, by its real pts: load paused a few frames earlier and
    /// step forward, reading the player's time-pos after every step, until the shown frame is reached. Null when the player doesn't
    /// report a time per step, the shown frame isn't where the session thinks it is, or the head is too close; the caller then steps one nominal frame.
    /// The result is the frame's pts and a load position inside that frame's interval (halfway to the next frame).
    /// </summary>
    private async Task<(double LoadAt, double Shown)?> TryExactBackAsync(PendingReload req)
    {
        SessionMedia media;
        double t;
        List<double>? known;
        lock (_gate) { media = _media!; known = _frames; if (_probeTimeouts >= ProbeTimeoutLimit) return null; }
        t = _player.TimePos;
        var frame = req.Frame;
        if (!(Math.Abs(t - req.Origin) <= 2.5 * frame)) return null;            // NaN fails too: nothing is on screen where we think

        var ct = _cts.Token;
        try
        {
            var tol = 0.25 * frame;
            var at = known is null ? -1 : Nearest(known, t, tol);
            if (at < req.Back || known is null)
            {
                var start = StartOf(media);
                var wanted = t - (req.Back + ProbeMarginFrames) * frame;
                var atHead = wanted <= start;
                var from = Snap(media, wanted);
                lock (_gate) EnsureWindow(from, false);
                if (!await _player.LoadAsync(_edl, from, false, EndLimitOf(media), ct).ConfigureAwait(false)) return null;
                var first = _player.TimePos;
                if (!double.IsFinite(first)) return null;
                var list = new List<double> { first };
                var cap = req.Back + ProbeMarginFrames + 24;
                while (list[^1] < t - tol && list.Count < cap)
                {
                    var next = await StepAsync(list[^1], ct).ConfigureAwait(false);
                    if (next is null) { lock (_gate) _probeTimeouts++; return null; }
                    list.Add(next.Value);
                }
                lock (_gate) _probeTimeouts = 0;
                known = list;
                at = Nearest(list, t, tol);
                if (at < 0) return null;
                if (at < req.Back && !atHead) return null;
                lock (_gate) _frames = list;
            }
            var k = Math.Max(0, at - req.Back);
            if (k >= known!.Count) return null;
            // mpv's start seek drops the frames before the start and shows the first one at/after it: load a quarter of the
            // previous gap before the frame's pts, which still lies after the frame before.
            var gap = k > 0 ? known[k] - known[k - 1] : frame;
            return (known[k] - 0.25 * gap, known[k]);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    /// <summary>Frame-steps the player and returns the time it reports for the new frame, or null on a timeout.</summary>
    private async Task<double?> StepAsync(double last, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(double v) { if (double.IsFinite(v) && v > last + 1e-6) tcs.TrySetResult(v); }
        _player.TimePosChanged += Handler;
        try
        {
            _player.FrameStep();
            return await tcs.Task.WaitAsync(FrameProbeTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        finally { _player.TimePosChanged -= Handler; }
    }

    /// <summary>Index of the frame in <paramref name="frames"/> nearest to <paramref name="t"/> within <paramref name="tolerance"/>, else -1.</summary>
    private static int Nearest(List<double> frames, double t, double tolerance)
    {
        var i = frames.BinarySearch(t);
        if (i < 0) i = ~i;
        var best = -1;
        var bestD = double.MaxValue;
        for (var j = Math.Max(0, i - 1); j <= Math.Min(frames.Count - 1, i); j++)
        {
            var d = Math.Abs(frames[j] - t);
            if (d < bestD) { bestD = d; best = j; }
        }
        return bestD <= tolerance ? best : -1;
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
            if (_loopEngaged && _loopA is { } la && _loopB is { } lb && t >= lb) auto = new PendingReload(la, ReloadCause.Loop, false, null);
            else if (reason != ReloadReason.None && Worthwhile(reason, _media, t))
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
        if (!eof) return;
        double pos = 0;
        double? loopA = null;
        lock (_gate)
        {
            if (_reloading || _disposed) return;
            if (_loopEngaged && _loopA is { } la && _loopB is not null) loopA = la;       // the end came before B: still a lap
            else
            {
                _atEnd = true;
                pos = double.IsNaN(_player.TimePos) ? _position : _player.TimePos;
                _position = pos;
            }
        }
        if (loopA is { } a)
        {
            try { _ = Enqueue(new PendingReload(a, ReloadCause.Loop, false, null), default).ContinueWith(static _ => { }, TaskScheduler.Default); }
            catch (ObjectDisposedException) { /* closing */ }
            return;
        }
        SafeInvoke(ReachedEnd, pos);
    }

    private static void SafeInvoke<T>(Action<T>? handler, T arg)
    {
        try { handler?.Invoke(arg); } catch (Exception) { /* subscriber bug */ }
    }

    private static double StartOf(SessionMedia? m) => m is { FirstChunk: { } f } ? m.ChunkStartSeconds(f) : 0;
    private static double EndOf(SessionMedia? m) => m?.EndSeconds ?? 0;

    /// <summary>The end to give mpv: only a saved clip's intended end, never the chunks' own (a cache session may grow past it).</summary>
    private static double? EndLimitOf(SessionMedia? m) => m is { EndLimitSeconds: not null } ? m.EndSeconds : null;

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
