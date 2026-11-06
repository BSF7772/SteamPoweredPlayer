// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S1 part A: correctness self-test (no timing results are taken from it; latencies are logged as INFORMATIONAL only).
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Graphics;

namespace S1App;

public sealed record CheckResult(string Name, bool Pass, string Detail);

/// <summary>
/// basics -> overlay -> static resizes -> drag-resize -> fullscreen x2 -> seeks -> final device state.
/// Writes out/&lt;tag&gt;.selftest.txt|json and PNG screenshots out/&lt;tag&gt;-*.png. Returns 0 only if every check passed.
/// </summary>
public static class SelfTest
{
    static readonly List<CheckResult> Results = [];
    static string _out = "", _tag = "";

    static void Check(string name, bool pass, string detail)
    {
        Results.Add(new(name, pass, detail));
        Log.W($"CHECK {(pass ? "PASS" : "FAIL")} {name}: {detail}");
    }

    static string Png(string n) => Path.Combine(_out, $"{_tag}-{n}.png");
    static string F(double d) => d.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>Screen rectangle (physical px) of an element: XAML bounds * rasterization scale + client origin.</summary>
    static async Task<Rectangle> ScreenRect(MainWindow w, Microsoft.UI.Xaml.FrameworkElement e)
        => await w.Ui(() =>
        {
            var s = w.Content.XamlRoot.RasterizationScale;
            var b = e.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
            var (ox, oy) = W32.ClientOrigin(w.Hwnd);
            return new Rectangle(ox + (int)Math.Round(b.X * s), oy + (int)Math.Round(b.Y * s), (int)Math.Round(b.Width * s), (int)Math.Round(b.Height * s));
        });

    static Task<Rectangle> PanelRect(MainWindow w) { W32.SetForegroundWindow(w.Hwnd); return ScreenRect(w, w.Panel); }

    /// <summary>Panel size in physical px from XAML (what the swap chain must match).</summary>
    static async Task<(int W, int H)> PanelPx(MainWindow w) => await w.Ui(() =>
    {
        var s = w.Content.XamlRoot.RasterizationScale;
        return ((int)Math.Round(w.Panel.ActualWidth * s), (int)Math.Round(w.Panel.ActualHeight * s));
    });

    static async Task<bool> WaitFor(Func<bool> cond, int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end) { if (cond()) return true; await Task.Delay(25); }
        return cond();
    }

    /// <summary>Frame-level health after a layout change: size matched, presents flowing, no black probe frames, device OK, screen non-black.</summary>
    static async Task StateCheck(MainWindow w, string name, int blackBefore, int settleMs = 400)
    {
        await Task.Delay(settleMs);
        var r = w.Rend!;
        var px = await PanelPx(w);
        bool sizeOk = r.PixelSize == px;
        long p0 = r.PresentCount;
        bool flowing = await WaitFor(() => r.PresentCount > p0 + 2, 1000);
        var sum = r.Summary();
        var shot = Shot.Capture(await PanelRect(w), Png(name));
        Check($"{name}", sizeOk && flowing && sum.BlackFrames - blackBefore == 0 && sum.DeviceReason == "0x00000000" && shot.NonBlack > 0.5 && shot.StdLuma > 8,
            $"swapchain={r.PixelSize.W}x{r.PixelSize.H} panel={px.W}x{px.H} presentsFlowing={flowing} blackFramesDelta={sum.BlackFrames - blackBefore} " +
            $"deviceReason={sum.DeviceReason} screenMeanLuma={F(shot.MeanLuma)} std={F(shot.StdLuma)} nonBlack={F(shot.NonBlack)}");
    }

    /// <summary>Resize the client area, then wait until the XAML panel really has that physical size (diagnostics logged).</summary>
    static async Task Resize(MainWindow w, int cw, int ch, bool wait = true)
    {
        await w.Ui(() => w.AppWindow.ResizeClient(new SizeInt32(cw, ch)));
        if (!wait) return;
        bool ok = false;
        for (int i = 0; i < 60 && !ok; i++)
        {
            var p = await PanelPx(w);
            ok = Math.Abs(p.W - cw) <= 2 && Math.Abs(p.H - ch) <= 2;
            if (!ok) await Task.Delay(25);
        }
        var diag = await w.Ui(() => $"pos={w.AppWindow.Position.X},{w.AppWindow.Position.Y} size={w.AppWindow.Size.Width}x{w.AppWindow.Size.Height} client={w.AppWindow.ClientSize.Width}x{w.AppWindow.ClientSize.Height} scale={w.Content.XamlRoot.RasterizationScale:F2}");
        var px = await PanelPx(w);
        Log.W($"RESIZE req={cw}x{ch} landed={ok} panel={px.W}x{px.H} {diag}");
    }

    public static async Task<int> RunAsync(MainWindow w)
    {
        _out = w.Opt.Out; _tag = w.Opt.Tag;
        var r = w.Rend!; var m = w.Mpv!;
        await w.Ui(() =>
        {
            if (w.AppWindow.Presenter is OverlappedPresenter op) op.IsAlwaysOnTop = true;   // keep it un-occluded for CopyFromScreen
            w.Activate();
        });
        W32.SetForegroundWindow(w.Hwnd);
        await Task.Delay(500);
        await Resize(w, 1280, 720);   // logs window diagnostics once up front

        // 1. basics ---------------------------------------------------------------------------------------------
        var mark = w.Load(w.T0);
        long restart = m.WaitEvent(MpvConst.EvPlaybackRestart, mark, 5000);
        Log.W($"INFORMATIONAL open->playback-restart {(restart > 0 ? MpvPlayer.Ms(restart - mark).ToString("F0", CultureInfo.InvariantCulture) : "TIMEOUT")} ms");
        Check("basics.restart", restart > 0, "playback-restart event after loadfile");
        await Task.Delay(1000);
        double t1 = m.GetDouble("time-pos"); long pc1 = r.PresentCount;
        await Task.Delay(1000);
        double t2 = m.GetDouble("time-pos");
        Check("basics.playing", t2 - t1 > 0.5 && r.PresentCount > pc1 + 5,
            $"time-pos {F(t1)} -> {F(t2)}, presents +{r.PresentCount - pc1}, hwdec-current={m.GetString("hwdec-current")}, video={m.GetString("video-params/w")}x{m.GetString("video-params/h")}");
        r.ResetStats();
        await Task.Delay(500);
        var shot0 = Shot.Capture(await PanelRect(w), Png("basics"));
        Check("basics.video-visible", shot0.NonBlack > 0.5 && shot0.StdLuma > 8, $"screen meanLuma={F(shot0.MeanLuma)} std={F(shot0.StdLuma)} nonBlack={F(shot0.NonBlack)}");
        Check("basics.no-black-frames", r.Summary().BlackFrames == 0, $"probed={r.Summary().ProbedFrames} black={r.Summary().BlackFrames}");

        // 2. overlay (DWM composites XAML over the swap chain; verify at screen level, video paused so only the overlay differs)
        await w.Ui(() => w.SetPause(true));
        await Task.Delay(400);
        var ovRect = await ScreenRect(w, w.Overlay);
        var panelRect = await PanelRect(w);
        var a = Shot.Capture(panelRect, Png("overlay-on"));
        await w.Ui(() => w.Overlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed);
        await Task.Delay(500);
        var b = Shot.Capture(panelRect, Png("overlay-off"));
        await w.Ui(() => w.Overlay.Visibility = Microsoft.UI.Xaml.Visibility.Visible);
        var rel = new Rectangle(ovRect.X - panelRect.X, ovRect.Y - panelRect.Y, ovRect.Width, ovRect.Height);
        var inner = Rectangle.Inflate(rel, -4, -4);
        double inside = a.ChangedFraction(b, inner);
        // everything outside the overlay rect should be identical: compare a band above it
        var above = new Rectangle(0, 0, a.W, Math.Max(1, rel.Top - 4));
        double outside = a.ChangedFraction(b, above);
        var statsRect = await ScreenRect(w, w.StatsText);
        double white = a.WhiteFraction(new Rectangle(statsRect.X - panelRect.X, statsRect.Y - panelRect.Y, statsRect.Width, statsRect.Height));
        Check("overlay.drawn-over-video", inside > 0.9 && outside < 0.01 && white > 0.005,
            $"overlayRect={rel.Width}x{rel.Height}@{rel.X},{rel.Y} changedInside={F(inside)} changedOutside(above)={F(outside)} whiteTextPixels={F(white)}");
        await w.Ui(() => w.SetPause(false));
        await Task.Delay(500);

        // 3. static resizes -----------------------------------------------------------------------------------
        foreach (var (cw, ch) in new[] { (960, 540), (1600, 900), (800, 450), (1280, 720) })
        {
            int black0 = r.Summary().BlackFrames;
            await Resize(w, cw, ch);
            await StateCheck(w, $"resize-{cw}x{ch}", black0);
        }

        // 4. drag simulation: many small steps in quick succession
        {
            int black0 = r.Summary().BlackFrames; int steps = 0;
            for (int i = 0; i <= 40; i++, steps++)
            {
                await Resize(w, 1280 + (int)(320 * Math.Sin(i / 40.0 * Math.PI)), 720 + (int)(180 * Math.Sin(i / 40.0 * Math.PI)), wait: false);
                await Task.Delay(25);
            }
            await Resize(w, 1280, 720);
            await StateCheck(w, "resize-drag", black0, 500);
            Log.W($"drag steps={steps}");
        }

        // 5. fullscreen x2 via the real XAML button (UI automation invoke) ------------------------------------
        var (sw, sh) = W32.PrimaryScreen();
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            int black0 = r.Summary().BlackFrames;
            await w.Ui(() => ((IInvokeProvider)new ButtonAutomationPeer(w.FsButton).GetPattern(PatternInterface.Invoke)).Invoke());
            await Task.Delay(700);
            bool isFs = await w.Ui(() => w.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
            var px = await PanelPx(w);
            bool near = Math.Abs(r.PixelSize.W - sw) <= 2 && Math.Abs(r.PixelSize.H - sh) <= 2;
            await StateCheck(w, $"fullscreen-{cycle}", black0, 100);
            var ov = Shot.Capture(await ScreenRect(w, w.Overlay));
            Check($"fullscreen-{cycle}.mode", isFs && near, $"presenterFullScreen={isFs} swapchain={r.PixelSize.W}x{r.PixelSize.H} screen={sw}x{sh} panel={px.W}x{px.H} overlayStd={F(ov.StdLuma)}");
            int black1 = r.Summary().BlackFrames;
            await w.Ui(() => ((IInvokeProvider)new ButtonAutomationPeer(w.FsButton).GetPattern(PatternInterface.Invoke)).Invoke());
            await Task.Delay(700);
            bool back = await w.Ui(() => w.AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);
            await StateCheck(w, $"windowed-after-fs-{cycle}", black1, 100);
            Check($"windowed-after-fs-{cycle}.mode", back, $"presenterDefault={back}");
        }

        // 6. seeks: loadfile start= must land on the right time-pos (INFORMATIONAL latency; not a result under load) ----
        foreach (var off in new[] { 200.0, 30.0, 150.0, 30.0, 90.0, 0.0 })
        {
            double t = w.T0 + off;
            var mk = w.Load(t);
            long ev = m.WaitEvent(MpvConst.EvPlaybackRestart, mk, 3000);
            double lat = ev > 0 ? MpvPlayer.Ms(ev - mk) : double.NaN;
            await Task.Delay(400);
            double tp1 = m.GetDouble("time-pos");
            await Task.Delay(1000);
            double tp2 = m.GetDouble("time-pos");
            Check($"seek+{off:F0}", ev > 0 && tp1 >= t - 0.05 && tp1 <= t + 1.5 && tp2 > tp1 + 0.5,
                $"target={F(t)} landed={F(tp1)} then={F(tp2)} INFORMATIONAL latency={lat.ToString("F0", CultureInfo.InvariantCulture)}ms");
        }

        // 7. final device state --------------------------------------------------------------------------------
        var fin = r.Summary();
        Check("final.device", fin.DeviceRemoved == 0 && fin.PresentErrors == 0 && fin.DeviceReason == "0x00000000" && r.LastRenderResult >= 0,
            $"removed={fin.DeviceRemoved} presentErrors={fin.PresentErrors} reason={fin.DeviceReason} lastRenderResult={r.LastRenderResult} blackFrames={fin.BlackFrames}/{fin.ProbedFrames}");

        // report ------------------------------------------------------------------------------------------------
        bool ok = Results.All(c => c.Pass);
        var sb = new StringBuilder();
        foreach (var c in Results) sb.AppendLine($"{(c.Pass ? "PASS" : "FAIL")}  {c.Name}  {c.Detail}");
        sb.AppendLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
        File.WriteAllText(Path.Combine(_out, _tag + ".selftest.txt"), sb.ToString());
        File.WriteAllText(Path.Combine(_out, _tag + ".selftest.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        return ok ? 0 : 1;
    }
}
