// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S1: window = SwapChainPanel (libmpv video) + XAML overlay. Seed for Phase 2's PlayerView.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using S2Tool;
using Windows.Graphics;
using WinRT.Interop;

namespace S1App;

public sealed partial class MainWindow : Window
{
    public Options Opt { get; }
    public MpvPlayer? Mpv { get; private set; }
    public Renderer? Rend { get; private set; }
    public SessionMedia? Media { get; private set; }
    public string EdlPath { get; private set; } = "";
    /// <summary>Session media axis (= EDL axis in lead mode): first frame ~T0, end TEnd.</summary>
    public double T0 { get; private set; }
    public double TEnd { get; private set; }
    public nint Hwnd { get; }
    public bool Paused { get; private set; }
    readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly TaskCompletionSource _ready = new();
    public Task Ready => _ready.Task;

    public MainWindow(Options o)
    {
        Opt = o;
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        Title = "S1 - WinUI 3 + libmpv D3D11";
        AppWindow.ResizeClient(new SizeInt32(1280, 720));
        Panel.Loaded += (_, _) => Start();
        Panel.SizeChanged += (_, _) => SyncSize();
        Panel.CompositionScaleChanged += (_, _) => SyncSize();
        FsButton.Click += (_, _) => SetFullscreen(AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);
        PauseButton.Click += (_, _) => SetPause(!Paused);
        _statsTimer.Tick += (_, _) => UpdateStats();
        Closed += (_, _) => { _statsTimer.Stop(); Rend?.Dispose(); Mpv?.Dispose(); };
    }

    (int W, int H, float S) PanelPixels()
    {
        float s = (float)(Panel.XamlRoot?.RasterizationScale ?? 1.0);
        return ((int)Math.Round(Panel.ActualWidth * s), (int)Math.Round(Panel.ActualHeight * s), s);
    }

    void SyncSize() { if (Rend is null) return; var (w, h, s) = PanelPixels(); if (w > 0 && h > 0) Rend.Resize(w, h, s, s); }

    void Start()
    {
        try
        {
            Directory.CreateDirectory(Opt.Out);
            Log.Init(Path.Combine(Opt.Out, Opt.Tag + ".log"));
            Log.W($"start mode={Opt.Mode} hwdec={Opt.HwDec}");
            Media = Scanner.Scan(Opt.Fixture, Opt.First, Opt.Last);
            int first = Media.Runs[0].First, last = Media.Runs[^1].First + Media.Runs[^1].Count - 1;
            T0 = (first - 1) * Media.SegmentSeconds; TEnd = last * Media.SegmentSeconds;
            EdlPath = Path.Combine(Opt.Out, Opt.Tag + ".edl").Replace('\\', '/');
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(EdlPath, Edl.Build(Media, lead: true), utf8);
            var warm = Path.Combine(Opt.Out, Opt.Tag + ".warm.edl").Replace('\\', '/');
            File.WriteAllText(warm, Edl.Build(Scanner.Scan(Opt.Fixture, first, first + 1), lead: true), utf8);

            Mpv = new MpvPlayer(Opt.HwDec, [$"log-file={Path.Combine(Opt.Out, Opt.Tag + ".mpv.log").Replace('\\', '/')}", "msg-level=all=v"]);
            Rend = new Renderer(Panel, Mpv) { ProbeBlack = true };
            var (w, h, s) = PanelPixels();
            Rend.Initialize(w, h, s, s);
            Log.W($"renderer ready {w}x{h} scale={s} adapter={Rend.Adapter}");

            // S2 rule: pre-warm mpv with a tiny EDL so the first real open is fast.
            var mark = Mpv.Mark();
            Mpv.Command("loadfile", warm, "replace", "-1", Inv($"start={T0}"));
            Log.W($"prewarm restart={(Mpv.WaitEvent(MpvConst.EvPlaybackRestart, mark, 5000) > 0)}");

            _statsTimer.Start();
            if (Opt.Mode == "interactive") Load(T0);
            _ready.TrySetResult();
            if (Opt.Mode != "interactive") _ = Task.Run(async () =>
            {
                int rc;
                try { rc = Opt.Mode == "selftest" ? await SelfTest.RunAsync(this) : await PerfRun.RunAsync(this); }
                catch (Exception e) { Log.W("FATAL " + e); rc = 99; }
                Log.W("exit " + rc);
                await Ui(() => Close());
                await Task.Delay(500);
                Environment.Exit(rc);
            });
        }
        catch (Exception e) { Log.W("START FAILED " + e); _ready.TrySetException(e); Environment.Exit(98); }
    }

    public static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>Seek = reload (S2 rule): <c>loadfile edl replace -1 start=t</c>. Returns the Stopwatch mark taken just before the command.</summary>
    public long Load(double startSeconds)
    {
        var mark = Mpv!.Mark();
        Mpv.Command("loadfile", EdlPath, "replace", "-1", Inv($"start={startSeconds}"));
        if (Paused) SetPause(false);
        return mark;
    }

    public void SetPause(bool p) { Paused = p; Mpv?.Set("pause", p ? "yes" : "no"); PauseButton.Content = p ? "Play" : "Pause"; }

    public void SetFullscreen(bool fs)
    {
        AppWindow.SetPresenter(fs ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        FsButton.Content = fs ? "Window" : "Fullscreen";
    }

    void UpdateStats()
    {
        if (Mpv is null || Rend is null) return;
        var (w, h) = Rend.PixelSize;
        StatsText.Text = Inv($"t={Mpv.GetDouble("time-pos"):F1}s  {w}x{h}  hw={Mpv.GetString("hwdec-current")}  presents={Rend.PresentCount}");
    }

    public Task<T> Ui<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!_ui.TryEnqueue(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } })) tcs.SetException(new InvalidOperationException("dispatcher closed"));
        return tcs.Task;
    }
    public Task Ui(Action a) => Ui<int>(() => { a(); return 0; });
}
