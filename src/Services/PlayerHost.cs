// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// The one mpv + renderer + session of the app (plan §3 "Playback path" item 0): created at app start on a worker thread and pre-warmed
/// on a tiny 2-chunk EDL as soon as the library knows a recording, so the first real open is fast (S2: 302 ms fresh, 95 ms pre-warmed).
/// The Player page binds its <c>SwapChainPanel</c> to <see cref="Renderer"/> while it is shown; navigating away unbinds it and leaves
/// everything else alive.
/// </summary>
/// <remarks>
/// Threading: <see cref="Mpv"/>, <see cref="Session"/> and <see cref="Follower"/> raise their events on mpv's own thread; subscribers must
/// marshal to the UI thread and must not call <c>DisposeAsync</c> from an event. Order of disposal: renderer, then player.
/// </remarks>
public sealed class PlayerHost
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSwapChainFn(nint self, nint swapChain);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Core.DecodeMode _appliedMode;
    private bool _openRequested;
    private int _shutdown;

    public static PlayerHost? Instance { get; private set; }

    public MpvPlayer? Mpv { get; private set; }
    public MpvD3D11Renderer? Renderer { get; private set; }
    public PlaybackSession? Session { get; private set; }
    public CacheFollower? Follower { get; private set; }
    public PlaybackMetrics Metrics { get; } = new();
    public ResumePositions Positions { get; }

    /// <summary>Completes when mpv, the renderer and the session exist (faulted when libmpv or the GPU is missing: see <see cref="Failure"/>).</summary>
    public Task Ready => _ready.Task;

    public Exception? Failure { get; private set; }

    /// <summary>Milliseconds the 2-chunk pre-warm took (null: not done, or skipped because a recording was opened first).</summary>
    public double? PreWarmMilliseconds { get; private set; }

    public string AdapterName => Renderer?.AdapterName ?? "";

    /// <summary>The renderer's device was removed or reset (render thread). The app must recreate the host; today the page says so.</summary>
    public event Action? DeviceLost;

    private PlayerHost()
    {
        var path = System.IO.Path.Combine(AppServices.Options.Sample
            ? SampleSession.DefaultRoot
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPoweredPlayer"), "positions.json");
        Positions = new ResumePositions(path);
    }

    /// <summary>Starts creating mpv and the renderer in the background (idempotent). Call once from <c>App.OnLaunched</c> after <c>AppServices.Init</c>.</summary>
    public static PlayerHost Start()
    {
        if (Instance is { } existing) return existing;
        PlayerLog.Init(AppServices.Options.Sample);
        // A crash in XAML code leaves no trace otherwise: log it (the app still fails fast afterwards).
        Microsoft.UI.Xaml.Application.Current.UnhandledException += (_, e) => PlayerLog.Write("UNHANDLED XAML exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => PlayerLog.Write("UNHANDLED exception: " + e.ExceptionObject);
        var host = Instance = new PlayerHost();
        _ = Task.Run(host.Create);
        return host;
    }

    private static Core.DecodeMode WantedMode =>
        AppServices.Settings.Decoding;

    private void Create()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            _appliedMode = WantedMode;
            var mpv = Mpv = new MpvPlayer(new MpvPlayerOptions { Decode = _appliedMode, LogLevel = "warn" });
            mpv.LogMessage += m => PlayerLog.Write($"mpv {m.Level} [{m.Prefix}] {m.Text}");
            mpv.PlaybackRestart += OnPlaybackRestart;
            // The renderer must exist before the first load (vo=libmpv). It starts at a default size; the page resizes it.
            var renderer = Renderer = new MpvD3D11Renderer(mpv, 1280, 720, 1f);
            renderer.DeviceLost += () =>
            {
                PlayerLog.Write("renderer: device lost, reason " + renderer.DeviceRemovedReason());
                DeviceLost?.Invoke();
            };
            var session = Session = new PlaybackSession(mpv);
            Follower = new CacheFollower(session);
            Metrics.Measured += s =>
                PlayerLog.Write(string.Create(CultureInfo.InvariantCulture, $"metric {s.Kind.ToString().ToLowerInvariant()} -> first frame {s.Milliseconds:0.0} ms (decode {_appliedMode})"));
            PlayerLog.Write(string.Create(CultureInfo.InvariantCulture, $"player host ready in {sw.ElapsedMilliseconds} ms, adapter {renderer.AdapterName}, decode {_appliedMode}"));
            _ready.TrySetResult();
        }
        catch (Exception e)
        {
            Failure = e;
            PlayerLog.Write("player host failed: " + e.Message);
            _ready.TrySetException(e);
        }
    }

    private void OnPlaybackRestart() => Metrics.Complete();

    // ---- pre-warm -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens mpv once on the first two chunks of a quiet recording (plan §3 item 0) so the decoder, the D3D11 device and mpv's demuxer are warm.
    /// Skipped when the user already opened something. Call it when a library source has its index (and again is harmless).
    /// </summary>
    public async Task PreWarmAsync(ILibrarySource library)
    {
        try
        {
            await Ready.ConfigureAwait(false);
            var media = PickPreWarmMedia(library);
            if (media is null) return;
            var (edl, start) = PlaybackSession.BuildPreWarm(media);
            Task<bool> load;
            var sw = Stopwatch.StartNew();
            lock (_gate)
            {
                if (_openRequested || PreWarmMilliseconds is not null) return;
                load = Mpv!.PreWarmAsync(edl, start);          // the loadfile is queued under the lock: a later open supersedes it
            }
            var ok = await load.ConfigureAwait(false);
            if (ok) PreWarmMilliseconds = sw.Elapsed.TotalMilliseconds;
            PlayerLog.Write(string.Create(CultureInfo.InvariantCulture, $"pre-warm {(ok ? "done" : "superseded")} in {sw.Elapsed.TotalMilliseconds:0} ms"));
        }
        catch (Exception e)
        {
            PlayerLog.Write("pre-warm failed: " + e.Message);
        }
    }

    /// <summary>A finished clip or session (not the live one) with at least two chunks; newest first, a handful of candidates.</summary>
    private static SessionMedia? PickPreWarmMedia(ILibrarySource library)
    {
        var index = library.Current;
        if (index is null) return null;
        foreach (var r in index.Recordings.Where(r => !r.Live).OrderByDescending(r => r.StartUtc).Take(12))
            if (library.GetMedia(r.RelativeFolder) is { ChunkCount: >= 2 } m) return m;
        return null;
    }

    // ---- opening and seeking (every seek is a reload: PlaybackSession) --------------------------------------------

    /// <summary>
    /// Opens <paramref name="media"/> and completes at the first frame. Applies the current decode setting first and measures open → first
    /// frame in <see cref="Metrics"/>.
    /// </summary>
    public async Task OpenAsync(SessionMedia media, double? startMediaSeconds, bool play, CancellationToken ct = default)
    {
        await Ready.ConfigureAwait(false);
        lock (_gate) _openRequested = true;
        ApplyDecodeMode();
        Metrics.Begin(MetricKind.Open);
        try { await Session!.OpenAsync(media, startMediaSeconds, play, ct).ConfigureAwait(false); }
        catch { Metrics.Cancel(); throw; }
    }

    public Task SeekAsync(double mediaSeconds)
    {
        Metrics.Begin(MetricKind.Seek);
        return Session!.SeekAsync(mediaSeconds);
    }

    public Task SeekRelativeAsync(double deltaSeconds)
    {
        Metrics.Begin(MetricKind.Seek);
        return Session!.SeekRelativeAsync(deltaSeconds);
    }

    public Task StepBackAsync(double frameSeconds)
    {
        Metrics.Begin(MetricKind.Seek);
        return Session!.StepBackAsync(frameSeconds);
    }

    /// <summary>Pauses and releases the recording's files (mpv <c>stop</c>); the host stays warm.</summary>
    public async Task StopAsync()
    {
        if (Mpv is null || Session is null) return;
        Metrics.Cancel();
        try
        {
            Session.SetPause(true);
            await Mpv.CommandAsync("stop").ConfigureAwait(false);
        }
        catch (Exception e) when (e is ObjectDisposedException or MpvException or TaskCanceledException) { /* closing */ }
    }

    private void ApplyDecodeMode()
    {
        var want = WantedMode;
        if (want == _appliedMode || Mpv is null) return;
        try
        {
            Mpv.SetString("hwdec", want == Core.DecodeMode.Cpu ? "no" : "d3d11va");     // takes effect on the next load
            PlayerLog.Write($"decode mode {_appliedMode} -> {want}");
            _appliedMode = want;
        }
        catch (MpvException e) { PlayerLog.Write("hwdec change failed: " + e.Message); }
    }

    public Core.DecodeMode DecodeMode => _appliedMode;

    // ---- swap chain panel -----------------------------------------------------------------------------------------

    /// <summary>Binds the renderer's swap chain to the panel (UI thread). A swap chain belongs to one panel at a time: call <see cref="DetachPanel"/> first.</summary>
    public void AttachPanel(SwapChainPanel panel)
    {
        if (Renderer is null) throw new InvalidOperationException("The renderer is not ready.");
        Bind(panel, Renderer.SwapChain);
    }

    public void DetachPanel(SwapChainPanel panel)
    {
        try { Bind(panel, 0); }
        catch (Exception e) when (e is COMException or InvalidCastException or ObjectDisposedException) { PlayerLog.Write("unbind panel: " + e.Message); }
    }

    // ISwapChainPanelNative {63aad0b8-7c24-40ff-85a8-640d944cc325}: vtable slot 3 = SetSwapChain(IDXGISwapChain*)
    private static void Bind(SwapChainPanel panel, nint swapChain)
    {
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        var unk = WinRT.MarshalInspectable<object>.FromManaged(panel);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in iid, out var native));
            try
            {
                var vtbl = Marshal.ReadIntPtr(native);
                var fn = Marshal.GetDelegateForFunctionPointer<SetSwapChainFn>(Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
                Marshal.ThrowExceptionForHR(fn(native, swapChain));
            }
            finally { Marshal.Release(native); }
        }
        finally { Marshal.Release(unk); }
    }

    /// <summary>The panel's size in device pixels and its DPI scale changed (SizeChanged, CompositionScaleChanged, fullscreen).</summary>
    public void Resize(int widthPixels, int heightPixels, float scale)
    {
        try { Renderer?.Resize(widthPixels, heightPixels, scale); }
        catch (Exception e) when (e is COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException or ObjectDisposedException)   // ResizeBuffers on a lost device
        {
            PlayerLog.Write("resize failed: " + e.Message);
        }
    }

    // ---- shutdown -------------------------------------------------------------------------------------------------

    /// <summary>Flushes positions, then disposes the session, the renderer and finally mpv (waits up to 3 s). Call from the window's Closed handler.</summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        Positions.Flush();
        try
        {
            Follower?.Dispose();
            Session?.DisposeAsync().AsTask().Wait(1000);
            Renderer?.Dispose();                                   // before the player: the render context must go first
            Mpv?.DisposeAsync().AsTask().Wait(3000);
        }
        catch (Exception e) when (e is AggregateException or ObjectDisposedException) { PlayerLog.Write("shutdown: " + e.Message); }
    }
}
