// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SteamPoweredPlayer.Core.Diagnostics;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Views;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// <c>SteamPoweredPlayer.exe --uicheck &lt;out.txt&gt; [--captures &lt;dir&gt;]</c> (plan 2026-11-07 §4.3): launches the real window on the real
/// library and drives it through UI Automation (automation peers found by <c>AutomationProperties.AutomationId</c>): Library → open the
/// shortest clip → +5 → A-B → back → Settings → Library → open the active cache session → back. Fails on a crash or an unhandled
/// exception, a wrong page, or a title-bar chip that changes width. The parent process (no window) starts a child with the window and
/// watches it, so a crash that kills the child still ends in a report. Window captures go to the captures folder (default %TEMP%),
/// never into the repository; the report holds no names or paths.
/// </summary>
internal static partial class UiCheck
{
    /// <summary>The child must finish within this; the D-TEST target is 90 s.</summary>
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(120);

    // =====================================================================================
    //  parent: no window, watches the child
    // =====================================================================================

    /// <summary>
    /// The journey in three segments, one app process each, so a crash in one (e.g. Player → back) still lets the others run:
    /// <c>clip</c> = Library → shortest clip → +5 → A-B → back; <c>settings</c> = Settings → Library; <c>cache</c> = active cache → back.
    /// </summary>
    public static readonly string[] Segments = ["clip", "settings", "cache"];

    public static int RunParent(DiagnosticsRequest request)
    {
        var report = Path.GetFullPath(request.ReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        if (request.Captures is { } caps) Directory.CreateDirectory(caps);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var sw = Stopwatch.StartNew();
        var headers = new List<string>();
        var checks = new List<string>();
        var crashed = false;
        foreach (var segment in Segments)
        {
            var part = report + "." + segment + ".txt";
            foreach (var f in new[] { part, StepFile(part), ExceptionsFile(part), DiagnosticReport.JsonPathFor(part) }) File.Delete(f);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in DiagnosticsCli.ChildArguments(request with { ReportPath = part })) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--segment");
            psi.ArgumentList.Add(segment);
            using var child = Process.Start(psi) ?? throw new InvalidOperationException("the uicheck child did not start");
            var exited = child.WaitForExit(ChildTimeout);
            if (!exited)
            {
                try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                child.WaitForExit(5000);
            }
            var lines = File.Exists(part) ? File.ReadAllText(part).Split('\n') : [];
            var blank = Array.IndexOf(lines, "");
            if (headers.Count == 0 && blank > 1) headers.AddRange(lines[1..blank]);
            if (blank >= 0)
                checks.AddRange(lines.Skip(blank + 1).TakeWhile(l => l.Length > 0));
            // The child deletes its step file only when it finished its segment and wrote its final report.
            if (!(exited && !File.Exists(StepFile(part)) && child.ExitCode is 0 or 1))
            {
                crashed = true;
                var step = File.Exists(StepFile(part)) ? File.ReadAllText(StepFile(part)).Trim() : "start-up";
                checks.Add(exited
                    ? $"FAIL  App crashed  exit code 0x{child.ExitCode:X8} during step \"{step}\""
                    : $"FAIL  App hung  did not finish within {ChildTimeout.TotalSeconds:0} s, step \"{step}\"");
                if (File.Exists(ExceptionsFile(part)))
                    checks.Add("INFO  Last first-chance exceptions before it  " + string.Join(" | ", File.ReadAllLines(ExceptionsFile(part)).TakeLast(5)));
            }
            foreach (var f in new[] { part, StepFile(part), ExceptionsFile(part), DiagnosticReport.JsonPathFor(part) }) File.Delete(f);
        }
        checks.Add(string.Create(CultureInfo.InvariantCulture, $"INFO  Elapsed  {sw.Elapsed.TotalSeconds:0.#} s"));
        int Count(string label) => checks.Count(l => l.StartsWith(label, StringComparison.Ordinal));
        var code = crashed ? 2 : Count("FAIL") > 0 ? 1 : 0;
        var text = "SteamPoweredPlayer --uicheck (real window, real library)\n" + string.Concat(headers.Select(h => h + "\n")) + "\n"
            + string.Concat(checks.Select(c => c + "\n")) + "\n"
            + $"Summary: {Count("PASS")} passed, {Count("FAIL")} failed, {Count("SKIP")} skipped, {Count("INFO")} info\n"
            + $"Result: {(code == 0 ? "PASS" : "FAIL")} (exit code {code})\n";
        File.WriteAllText(report, text);
        return code;
    }

    /// <summary>The segment a child runs (<c>--segment &lt;name&gt;</c>); all of them when absent.</summary>
    private static string? SegmentArgument()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => string.Equals(a, "--segment", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string StepFile(string report) => report + ".step";
    private static string ExceptionsFile(string report) => report + ".exceptions";

    [ThreadStatic] private static bool _inFirstChance;

    // =====================================================================================
    //  child: the real window
    // =====================================================================================

    /// <summary>Called by the app after the window is activated in <c>--uicheck-child</c> mode.</summary>
    public static void Start(MainWindow window, DiagnosticsRequest request)
    {
        var driver = new Driver(window, request);
        Application.Current.UnhandledException += (_, e) =>
        {
            driver.Fail("uicheck.unhandled", "Unhandled exception (UI thread)", Redactor.Describe(e.Exception));
            e.Handled = true;          // keep driving: the report should show everything that is wrong
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            driver.Fail("uicheck.crash", "Unhandled exception (process)", e.ExceptionObject is Exception x ? Redactor.Describe(x) : "unknown", final: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            driver.Fail("uicheck.unobserved", "Unobserved task exception", Redactor.Describe(e.Exception.GetBaseException()));
            e.SetObserved();
        };
        // A WinUI fail-fast (stowed exception) kills the process without any handler: the parent prints the last of these.
        var exceptions = ExceptionsFile(Path.GetFullPath(request.ReportPath));
        var logged = 0;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (_inFirstChance || Interlocked.Increment(ref logged) > 300) return;
            _inFirstChance = true;
            try
            {
                var where = e.Exception.TargetSite is { } m ? $" at {m.DeclaringType?.Name}.{m.Name}" : "";
                File.AppendAllText(exceptions, $"thread {Environment.CurrentManagedThreadId}: {Redactor.Describe(e.Exception)}{where}{Environment.NewLine}");
            }
            catch (Exception) { /* never throw from here */ }
            finally { _inFirstChance = false; }
        };
        window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () => await driver.RunAsync());
    }

    private sealed partial class Driver(MainWindow window, DiagnosticsRequest request)
    {
        private readonly DiagnosticReport _report = new("uicheck", "SteamPoweredPlayer --uicheck (real window, real library)");
        private readonly string _path = Path.GetFullPath(request.ReportPath);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ChipWatch _chips = new();
        private int _capture;
        private readonly string? _segment = SegmentArgument();
        private string _step = "start-up";

        private static string F(double v, string f = "0.#") => BudgetRules.F(v, f);

        public void Fail(string id, string name, string value, bool final = false)
        {
            lock (_report)
            {
                _report.Add(CheckResult.Fail(id, name, value, note: "during step \"" + _step + "\""));
                if (final) Finish();
                else _report.WriteTo(_path);
            }
        }

        private void Add(CheckResult c)
        {
            lock (_report) { _report.Add(c); _report.WriteTo(_path); }
        }

        private void Step(string step)
        {
            _step = step;
            try { File.WriteAllText(_path + ".step", step); } catch (IOException) { }
        }

        private void Finish()
        {
            _report.Add(CheckResult.Info("uicheck.elapsed", $"Elapsed ({_segment ?? "all"})", F(_clock.Elapsed.TotalSeconds) + " s", number: _clock.Elapsed.TotalSeconds, unit: "s"));
            _report.WriteTo(_path);
        }

        public async Task RunAsync()
        {
            try { await JourneyAsync(); }
            catch (Exception e)
            {
                Fail("uicheck.driver", "The check itself failed", Redactor.Describe(e));
            }
            _chips.Stop();
            Add(_chips.Result() with { Name = $"Title-bar chips keep their width (F10, {_segment ?? "all"})" });
            Step("shutdown");
            lock (_report) Finish();
            var code = _report.ExitCode;
            try { window.Close(); }
            catch (Exception e) { Fail("uicheck.close", "Window closes cleanly", Redactor.Describe(e)); code = 1; }
            try { File.Delete(_path + ".step"); } catch (IOException) { }
            Environment.Exit(code);
        }

        private int _defaultWidth, _defaultHeight;

        // ---- the journey -------------------------------------------------------------------------------------------

        private string? _exportDir, _savedExportFolder;

        private async Task JourneyAsync()
        {
            AppSettings.Frozen = true;                  // nothing this check clicks may reach the user's settings.json
            _report.AddHeader("Hardware: " + HardwareClass.Detect().Describe());
            bool Runs(string s) => _segment is null || string.Equals(_segment, s, StringComparison.OrdinalIgnoreCase);
            Step("library");
            var libraryOk = await WaitFor(() => Page() is LibraryPage && AppServices.Library?.Current is { Recordings.Count: > 0 }, 20);
            Add(CheckResult.From(libraryOk, "library.shown", $"Library page shows the library ({_segment ?? "all"})",
                libraryOk ? $"{AppServices.Library!.Current!.Recordings.Count} recordings after {F(_clock.Elapsed.TotalSeconds, "0.0")} s" : $"page {PageName()}, no index"));
            if (!libraryOk) return;
            _chips.Start(window);
            await Capture("library");
            (_defaultWidth, _defaultHeight) = (window.AppWindow.Size.Width, window.AppWindow.Size.Height);
            await ResizeStress("library");
            var all = AppServices.Library!.Current!.Recordings;
            // the cache strip with its marker lane (only a capture: nothing to compute that the eye doesn't judge better)
            if (all.FirstOrDefault(r => r.Kind == Core.Steam.RecordingKind.CacheSession && AppServices.Library!.MarkersFor(r).Count > 0) is { } marked)
            {
                SelectGame(marked.GameId);
                await Task.Delay(400);
                await Capture("library-markers");
            }

            var clip = RealCheckRules.ShortestClip(all);
            var cache = RealCheckRules.ActiveCache(all);

            if (Runs("clip") && clip is not null)
            {
                Step("open-clip");
                await OpenFromLibrary("clip", clip, tile: true);
                if (await PlayerReady("clip", clip)) await PlayerChecks("clip", clip);
                Step("back-from-clip");
                await Back("clip");
                Step("library-exports");
                await LibraryExportsCheck("clip");
            }
            else if (Runs("clip")) Add(CheckResult.Skip("clip.open", "Open the shortest saved clip", "no saved clip of at least 6 s"));

            if (Runs("settings"))
            {
                for (var round = 1; round <= 3; round++)
                {
                    Step($"settings ({round}/3)");
                    await Nav("NavSettings", typeof(SettingsPage), $"nav.settings.{round}", $"Settings page opens from the title bar ({round}/3)");
                    if (round == 1) { await Capture("settings"); await DeployButtonCheck(); }
                    Step($"settings-to-library ({round}/3)");
                    await Nav("NavLibrary", typeof(LibraryPage), $"nav.library.{round}", $"Settings -> Library ({round}/3)");
                }
                await Capture("library-again");
            }

            if (Runs("cache") && cache is not null)
            {
                Step("open-cache");
                await OpenFromLibrary("cache", cache, tile: false);
                if (await PlayerReady("cache", cache))
                {
                    await Capture("cache");
                    await FrameBackButton("cache");
                    await ResizeStress("cache");
                }
                Step("back-from-cache");
                await Back("cache");
                Step("cache-save-clip");
                if (Find("LibrarySaveClip") is Control { IsEnabled: true } saveClip && Invoke(saveClip))
                {
                    var opened = await WaitFor(() => Page() is PlayerPage, 5);
                    Add(CheckResult.From(opened, "cache.save-clip", "Save clip on the Cache strip opens the Player", opened ? "Player shown" : "still in the Library"));
                    if (opened && await WaitFor(() => Find("PlayerBack") is not null, 5)) await Back("cache-save");
                }
            }
            else if (Runs("cache")) Add(CheckResult.Skip("cache.open", "Open the active cache session", "no cache session"));
        }

        private async Task OpenFromLibrary(string id, RecordingEntry entry, bool tile)
        {
            SelectGame(entry.GameId);
            await Task.Delay(400);
            var name = tile ? "Open the shortest clip from its tile (LibraryClipTile)" : "Open the active cache session from its block (LibraryCacheBlock)";
            var target = tile ? FindClipTile(entry.RelativeFolder) : FindByIdAndTag("LibraryCacheBlock", t => t is RecordingEntry r && r.RelativeFolder == entry.RelativeFolder);
            if (target is not null && Invoke(target))
            {
                Add(CheckResult.Pass($"{id}.open", name, "invoked through its automation peer"));
                return;
            }
            Add(CheckResult.Fail($"{id}.open", name, target is null ? "no element with that AutomationId for this recording" : "the element has no Invoke pattern",
                note: "opened through MainWindow.NavigateToPlayer instead, so the rest of the journey still runs"));
            window.NavigateToPlayer(new PlayerRequest(entry.RelativeFolder));
        }

        private async Task<bool> PlayerReady(string id, RecordingEntry entry)
        {
            var sw = Stopwatch.StartNew();
            var ok = await WaitFor(() => Page() is PlayerPage && Session() is { IsReloading: false, Media: { } m } && SameRecording(m, entry)
                                         && double.IsFinite(PlayerHost.Instance?.Mpv?.TimePos ?? double.NaN), 12);
            Add(CheckResult.From(ok, $"{id}.player", "Player page plays the recording",
                ok ? $"first frame {F(sw.Elapsed.TotalMilliseconds, "0")} ms after navigation, hwdec-current {PlayerHost.Instance?.Mpv?.GetString("hwdec-current") ?? "?"}" : $"page {PageName()}"));
            return ok;
        }

        private static bool SameRecording(SessionMedia m, RecordingEntry e) =>
            m.Folder.Replace('\\', '/').Contains(e.RelativeFolder, StringComparison.OrdinalIgnoreCase);

        private async Task PlayerChecks(string id, RecordingEntry entry)
        {
            var s = Session()!;
            var mpv = PlayerHost.Instance!.Mpv!;
            var fps = mpv.GetDouble("container-fps") is var f && double.IsFinite(f) && f > 1 ? f : 60;
            double MpvUi() => mpv.TimePos - s.MediaStartSeconds;

            // pause through the UI, then the axis: UI zero, duration from the index
            if (!s.UserPaused && Find("PlayerPlayPause") is { } play) Invoke(play);
            await WaitFor(() => s.UserPaused && !s.IsReloading, 2);
            await s.SeekUiAsync(0);
            await Task.Delay(150);
            var timecode = (Find("PlayerTimecode") as TextBlock)?.Text ?? "";
            Add(CheckResult.From(RealCheckRules.WithinFrames(0, MpvUi(), fps) && (Math.Abs(s.UiDuration - entry.DurationSeconds) <= 1 / fps + 0.001 || entry.Runs.Count > 1),
                $"{id}.axis", "UI axis: 0 at the head, duration = index",
                $"mpv UI position {F(MpvUi(), "0.000")} s, UiDuration {F(s.UiDuration, "0.000")} s, index {F(entry.DurationSeconds, "0.000")} s"));
            Add(CheckResult.From(Timecode().IsMatch(timecode), $"{id}.timecode", "Timecode shows HH:MM:SS.FF / HH:MM:SS.FF (F2)",
                "\"" + Redactor.Scrub(timecode) + "\""));
            await Capture(id);

            await TransportBounds(id);
            await RulerChecks(id);

            // +5 through the button
            var expected = PlaybackSession.Snap(s.Media!, mpv.TimePos + 5) - s.MediaStartSeconds;
            var fwd = Find("PlayerForward5");
            var invoked = fwd is not null && Invoke(fwd);
            await Task.Delay(100);
            await WaitFor(() => !s.IsReloading, 3);
            await Task.Delay(150);
            Add(CheckResult.From(invoked && RealCheckRules.WithinFrames(expected, MpvUi(), fps), $"{id}.fwd5", "+5 button lands within 1 frame",
                invoked ? $"expected UI {F(expected, "0.000")} s, landed {F(MpvUi(), "0.000")} s" : "no PlayerForward5 element"));

            // A-B: A at UI 1 s, B at UI 3 s through the loop button
            var loop = Find("PlayerAbLoop");
            if (loop is null) { Add(CheckResult.Fail($"{id}.ab-loop", "A-B loop through the loop button", "no PlayerAbLoop element")); return; }
            await s.SeekUiAsync(1);
            await WaitFor(() => !s.IsReloading, 3);
            var aSet = Invoke(loop);
            await s.SeekUiAsync(3);
            await WaitFor(() => !s.IsReloading, 3);
            var bSet = Invoke(loop);
            await s.SeekUiAsync(1);
            await WaitFor(() => !s.IsReloading, 3);
            s.SetPause(false);
            var samples = new List<double>();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(4.6))
            {
                await Task.Delay(20);
                samples.Add(MpvUi());
            }
            s.SetPause(true);
            await Capture(id + "-ab");
            var ab = RealCheckRules.EvaluateAbLoop(samples, 1, 3, fps);
            Add(CheckResult.From(aSet && bSet && ab.Ok, $"{id}.ab-loop", "A-B loop through the loop button (A = 1 s, B = 3 s)",
                (aSet && bSet ? "" : "the button could not be pressed; ") + ab.Describe()));
            await ExportChecks(id, loop);
        }

        /// <summary>
        /// The Export card through its real controls, with the A-B range still set (1 s to 3 s): ladder, edits, sync, estimate, detect, Save
        /// clip, Export, Fit to size, a kept part, a screenshot. Output goes to a temp folder set in memory only: settings.json is frozen
        /// for the whole check, and the folder picker (which automation can't drive) never opens. The folder lives until the Library
        /// has listed the exports, see <see cref="LibraryExportsCheck"/>.
        /// </summary>
        private async Task ExportChecks(string id, FrameworkElement loop)
        {
            var dir = Path.Combine(Path.GetTempPath(), "spp-uicheck-export-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            _exportDir = dir;
            _savedExportFolder = AppServices.Settings.ExportFolder;
            AppServices.Settings.ExportFolder = dir;

            await Task.Delay(300);
            await WaitFor(() => Find("PlayerExport") is Button { IsEnabled: true }, 30);        // encoder detection may still be running

            // ---- the ladder
            var edits = Find("PlayerEdits") is { ActualHeight: > 0 };
            Add(CheckResult.From(edits, $"{id}.edits-visible", "Audio sync row is on screen under the video", edits ? "visible" : "hidden"));
            Press("ExportTier-medium");
            Press("ExportEngine-Auto");
            Press("ExportTier-quality");                                // redraws the card with the folder set above
            Press("ExportTier-medium");
            var folderShown = Text("PlayerExportFolderText").EndsWith(dir, StringComparison.Ordinal);
            Add(CheckResult.From(folderShown, $"{id}.export-folder-text", "The export folder is written on the card", folderShown ? "shown" : "not shown"));
            var expected = FastEncoder.Core.Encoders.BestGpu is null ? "Medium · CPU" : "Medium · GPU";
            Add(CheckResult.From(Text("PlayerExportChoice").StartsWith(expected, StringComparison.Ordinal), $"{id}.ladder-medium", "Medium on Auto uses the GPU when there is one", expected));
            Press("ExportTier-ultra-quality");
            var cpuOnly = Text("PlayerExportChoice").StartsWith("Ultra quality · CPU", StringComparison.Ordinal) && Find("ExportEngine-Gpu") is Control { IsEnabled: false };
            Add(CheckResult.From(cpuOnly, $"{id}.ladder-cpu-only", "Ultra quality is CPU only, its GPU switch is off", cpuOnly ? "CPU, switch off" : Text("PlayerExportChoice")));
            Press("ExportTier-medium");
            Press("ExportEngine-Auto");

            // ---- special presets slide out of a menu; Trim only and Compact are gone
            var closed = Find("ExportSpecial-archive") is null or { ActualHeight: 0 };
            if (Find("ExportSpecialExpander") is Expander expander) expander.IsExpanded = true;
            var opened = await WaitFor(() => Find("ExportSpecial-target") is { ActualHeight: > 0 }, 3);
            var removed = Find("ExportSpecial-trim") is null && Find("ExportSpecial-compact") is null;
            Add(CheckResult.From(closed && opened && removed, $"{id}.special-menu", "Special presets are in a closed menu, without Trim only and Compact",
                $"closed at first {closed}, opens {opened}, removed {removed}"));

            // ---- the first export warns that the player's settings go into the file
            AppServices.Settings.SkipPlayerSettingsWarning = false;
            Press("PlayerExport");
            var warned = await WaitFor(() => OpenDialog() is not null, 3);
            var hasCheck = warned && Descendants(OpenDialog()).OfType<CheckBox>().Any();
            OpenDialog()?.Hide();
            await WaitFor(() => OpenDialog() is null, 3);
            Add(CheckResult.From(warned && hasCheck && AppServices.Exports.Pending == 0, $"{id}.export-warning", "First export warns, with a Don't show again box; Cancel exports nothing",
                $"dialog {warned}, box {hasCheck}, queued {AppServices.Exports.Pending}"));
            AppServices.Settings.SkipPlayerSettingsWarning = true;

            // ---- estimate, detect
            var estimate = Find("PlayerEstimate");
            var estimated = estimate is not null && Invoke(estimate) && await WaitFor(() => Text("PlayerEstimateText") is { Length: > 0 } t && !t.StartsWith("Estimating", StringComparison.Ordinal), 60);
            Add(CheckResult.From(estimated, $"{id}.export-estimate", "Estimate size button gives a size", estimated ? "a size is shown" : "no size within 60 s"));
            var detect = Find("PlayerDetectSync");
            var detected = detect is not null && Invoke(detect) && await WaitFor(() => Text("PlayerSyncStatus") is { Length: > 0 } t && !t.StartsWith("Analysing", StringComparison.Ordinal), 60);
            Add(CheckResult.From(detected, $"{id}.export-detect", "Detect button reports a verdict", detected ? "a verdict is shown" : "no verdict within 60 s"));
            if (Find("PlayerSyncReset") is { } clear) Invoke(clear);
            await Task.Delay(1200);

            // ---- sync: applied 1 s after the last change, with a ring and a check mark, kept for the game
            var mpv = PlayerHost.Instance?.Mpv;
            var box = Find("PlayerSyncBox") as NumberBox;
            var before = mpv?.AudioDelay ?? double.NaN;
            Press("PlayerSyncLater");
            Press("PlayerSyncLater");
            await Task.Delay(100);
            var ring = Find("PlayerSyncBusy") is { ActualHeight: > 0 };
            var waited = Math.Abs((mpv?.AudioDelay ?? double.NaN) - before) < 0.0005;
            var game = (Page() as PlayerPage)?.CurrentGameId;
            var arrived = await WaitFor(() => Math.Abs((mpv?.AudioDelay ?? double.NaN) - 0.04) < 0.002, 2);
            await Task.Delay(50);
            var mark = Find("PlayerSyncApplied") is FontIcon { ActualHeight: > 0, Glyph: "\uE73E" };
            var stored = game is null || (AppServices.Settings.GameAudioDelays.TryGetValue(game, out var kept0) && Math.Abs(kept0 - 0.04) < 0.002);
            Add(CheckResult.From(ring && waited && arrived && mark && stored && box?.Value is 40, $"{id}.sync-nudge", "Sync waits 1 s, shows a ring then a check mark, reaches the player and the game",
                $"ring {ring}, waited {waited}, arrived {arrived}, check {mark}, game {stored}, box {box?.Value}"));
            if (box is not null) box.Value = 4321;
            var slider = Find("PlayerSyncSlider") as Slider;
            await Task.Delay(100);
            Add(CheckResult.From(slider is { Maximum: 5000, Minimum: -5000, StepFrequency: 10, SnapsTo: Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues } && box?.Value is 4321, $"{id}.sync-range",
                "The slider moves in 10 ms steps and reaches ±5 s; a typed value stays exact", $"slider {slider?.Value} of {slider?.Maximum} step {slider?.StepFrequency}, box {box?.Value}"));
            if (box is not null) box.Value = 137;
            await Task.Delay(100);
            var exact = await WaitFor(() => Math.Abs((mpv?.AudioDelay ?? double.NaN) - 0.137) < 0.0005, 3);
            Add(CheckResult.From(exact && box?.Value is 137, $"{id}.sync-typed-exact", "A typed 137 ms is applied as 137, not rounded to the slider's steps",
                $"delay {(mpv?.AudioDelay ?? double.NaN) * 1000:0.#} ms, box {box?.Value}, slider {slider?.Value}"));

            // ---- exports through the buttons, with the sound shifted: both must keep all their sound
            if (box is not null) box.Value = -300;
            await ExportButtonCheck(id, "save-clip", "PlayerSaveClip", "Save clip with sync -300 ms: plays from A to B exactly, sound all the way", dir, 1.9, 2.2, suffix: "");
            await ExportButtonCheck(id, "export", "PlayerExport", "Export Medium with sync -300 ms (A = 1 s, B = 3 s), sound all the way", dir, 1.7, 2.3, suffix: "_M");
            if (Find("PlayerSyncReset") is { } reset) Invoke(reset);
            await Task.Delay(1200);

            // ---- speed and sound come from the player's own controls
            Session()?.SetSpeed(2);
            Session()?.SetMute(true);
            var noted = await WaitFor(() => Text("PlayerExportRange").Contains("2×, no sound", StringComparison.Ordinal), 3);
            Add(CheckResult.From(noted, $"{id}.player-settings", "The card says the player's speed and mute go into the export", noted ? "shown" : Text("PlayerExportRange")));
            await ExportButtonCheck(id, "export-speed", "PlayerExport", "Export with the player at 2× and muted", dir, 0.8, 1.2, audio: false);
            Session()?.SetSpeed(1);
            Session()?.SetMute(false);
            await WaitFor(() => !Text("PlayerExportRange").Contains("no sound", StringComparison.Ordinal), 3);

            Press("ExportSpecial-target");
            if (Find("PlayerExportTargetMb") is NumberBox target) target.Value = 1;
            await ExportButtonCheck(id, "export-fit", "PlayerExport", "Fit to size stays under 1 MB, its name has no tier", dir, 1.7, 2.3, maxBytes: 1024 * 1024, suffix: "");
            Press("ExportTier-ultra-performance");
            await ExportButtonCheck(id, "export-up", "PlayerExport", "Ultra performance is named _UP", dir, 1.7, 2.3, suffix: "_UP");
            Press("ExportTier-medium");

            await ShareCheck(id);

            var keep = Find("PlayerKeepAb");
            var kept = keep is not null && Invoke(keep) && await WaitFor(() => Text("PlayerExportRange").StartsWith("1 kept part", StringComparison.Ordinal), 3);
            Add(CheckResult.From(kept, $"{id}.export-keep", "Keep A-B adds a kept part and clears the loop", kept ? "1 kept part" : "the range text did not change"));
            await Capture(id + "-kept");
            await ExportButtonCheck(id, "export-kept", "PlayerExport", "Export the kept part", dir, 1.7, 2.3);
            if (!kept) Invoke(loop);                                    // Keep A-B clears the loop; otherwise clear it here

            var shot = Find("PlayerScreenshot");
            var shotTaken = shot is not null && Invoke(shot) && await WaitFor(() => Directory.EnumerateFiles(dir, "*.png").Any(f => new FileInfo(f).Length > 0), 5);
            var toast = shotTaken && await WaitFor(() => Descendants(window.Content).OfType<InfoBar>().Any(b => b.Title == "Screenshot saved"), 3);
            var buttons = Find("PlayerShare") is { } lowest && window.Content is FrameworkElement top
                && lowest.TransformToVisual(top).TransformBounds(new Windows.Foundation.Rect(0, 0, lowest.ActualWidth, lowest.ActualHeight)).Bottom <= top.ActualHeight;
            Add(CheckResult.From(buttons, $"{id}.export-buttons", "Screenshot and Share stay on screen, with the special menu open", buttons ? "inside the window" : "cut off"));
            Add(CheckResult.From(shotTaken && toast, $"{id}.screenshot", "Screenshot button saves a frame and says so on the Player", $"PNG {shotTaken}, toast {toast}"));
            await ToastChecks(id, dir);
        }

        private IEnumerable<InfoBar> Toasts() => Descendants(window.Content).OfType<InfoBar>();

        /// <summary>Toasts about a file name it as a link and open it on a click (intercepted: no real app starts); non-error toasts close after 5 s, errors stay.</summary>
        private async Task ToastChecks(string id, string dir)
        {
            var shot = Directory.EnumerateFiles(dir, "*.png").FirstOrDefault();
            var bar = Toasts().FirstOrDefault(b => b.Title == "Screenshot saved");
            var link = bar?.ActionButton as HyperlinkButton;
            var named = shot is not null && link?.Content is TextBlock t && t.Text == Path.GetFileName(shot) && AutomationProperties.GetAutomationId(link) == "ToastOpenFile";
            Add(CheckResult.From(named, $"{id}.toast-link", "The screenshot toast names the file as a link button", named ? "link" : "no link button with the file name"));

            string? opened = null;
            FileOpener.Intercept = p => opened = p;
            try
            {
                var fake = Path.Combine(dir, "toast-check.png");
                AppServices.Toast("Toast click check", null, InfoBarSeverity.Success, overPlayer: true, openPath: fake);
                await Task.Delay(100);
                var mine = Toasts().FirstOrDefault(b => b.Title == "Toast click check");
                var clicked = mine?.ActionButton is { } button && Invoke(button);
                await Task.Delay(100);
                var stillUp = Toasts().Any(b => b.Title == "Toast click check");
                Add(CheckResult.From(clicked && opened == fake && !stillUp, $"{id}.toast-open",
                    "Clicking the link opens the file (intercepted) and closes the toast", $"opened {opened is not null}, still up {stillUp}"));

                opened = null;
                AppServices.Toast("Toast close check", null, InfoBarSeverity.Success, overPlayer: true, openPath: fake);
                await Task.Delay(100);
                if (Toasts().FirstOrDefault(b => b.Title == "Toast close check") is { } closing) closing.IsOpen = false;
                await Task.Delay(100);
                Add(CheckResult.From(opened is null, $"{id}.toast-close-no-open", "Closing a toast with its X does not open the file", opened is null ? "not opened" : "opened"));
            }
            finally { FileOpener.Intercept = null; }

            AppServices.Toast("Toast error check", "stays", InfoBarSeverity.Error);
            var closed = await WaitFor(() => !Toasts().Any(b => b.Title == "Screenshot saved"), 7);
            var errorStays = Toasts().Any(b => b.Title == "Toast error check");
            Add(CheckResult.From(closed && errorStays, $"{id}.toast-auto-close", "A toast closes by itself after 5 s; an error stays until dismissed", $"closed {closed}, error still up {errorStays}"));
            if (Toasts().FirstOrDefault(b => b.Title == "Toast error check") is { } err) err.IsOpen = false;
        }

        private ContentDialog? OpenDialog() =>
            window.Content.XamlRoot is { } root ? VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault() : null;

        /// <summary>Settings' phone button through the real script, stopped after it looks for the phone: nothing is built or installed here.</summary>
        private async Task DeployButtonCheck()
        {
            if (Find("SettingsDeployPhone") is not Control button) { Add(CheckResult.Fail("settings.deploy-phone", "Install on phone runs the deploy script", "no button (this checkout should have the script)")); return; }
            SettingsPage.DeployArguments = ["-CheckOnly"];
            try
            {
                var pressed = Invoke(button);
                var done = await WaitFor(() => button.IsEnabled && (Find("SettingsDeployStatus") as TextBlock)?.Text is { Length: > 0 } t && t != "Starting…" && !t.StartsWith("Looking", StringComparison.Ordinal), 30);
                var status = (Find("SettingsDeployStatus") as TextBlock)?.Text ?? "";
                // Which of these depends on whether a phone happens to be plugged in; both prove that the button reached adb. No device name goes in the report.
                var answered = status.StartsWith("Found ", StringComparison.Ordinal) || status.StartsWith("No phone found", StringComparison.Ordinal);
                Add(CheckResult.From(pressed && done && answered, "settings.deploy-phone", "Install on phone runs the deploy script and shows its answer", answered ? status.Split(' ')[0] + " ..." : status));
            }
            finally { SettingsPage.DeployArguments = []; }
        }

        /// <summary>Share up to the sheet, which automation couldn't close: a small H.264 + AAC 720p file, removed afterwards.</summary>
        private async Task ShareCheck(string id)
        {
            if (Environment.GetEnvironmentVariable("SPP_UICHECK_REAL_SHARE") == "1")
            {
                window.Activate();
                Press("PlayerShare");                                   // the real sheet: it closes with the window
                var provided = await WaitFor(() => ShareSheet.ClipProvided, 15);
                Add(CheckResult.From(provided, $"{id}.share-real", "Windows' share window asks for the clip and gets it", provided ? "provided" : "not asked within 15 s"));
                return;
            }
            string? shared = null;
            ShareSheet.Intercept = p => shared = p;
            try
            {
                Press("PlayerShare");
                var ready = await WaitFor(() => shared is not null, 60);
                var info = shared is null ? null : await FastEncoder.Core.Ff.ProbeAsync(shared);
                // The tier chosen in the export panel, whichever it is: only Ultra performance makes a 720p30 H.264 + AAC copy.
                var choice = Core.Export.ExportLadder.IsKnown(AppServices.Settings.ExportPreset) ? AppServices.Settings.ExportPreset : Core.Export.ExportLadder.DefaultId;
                var ok = choice == Core.Export.ExportLadder.ShareId
                    ? info is { VideoCodec: "h264", AudioCodec: "aac", Fps: <= 30.5 } && Math.Min(info.Width, info.Height) <= 720
                    : info is not null && (info.VideoCodec != "h264" || info.Fps > 30.5 || Math.Min(info.Width, info.Height) > 720);
                Add(CheckResult.From(ready && ok, $"{id}.share", $"Share makes a copy with the chosen tier ({choice}) for the share window",
                    info is null ? "no file within 60 s" : $"{info.VideoCodec}/{info.AudioCodec} {info.Width}x{info.Height} {F(info.Fps, "0.#")} fps, {new FileInfo(shared!).Length / 1024} KiB"));
                ShareSheet.Sweep();
                var gone = shared is not null && !File.Exists(shared);
                Add(CheckResult.From(gone, $"{id}.share-removed", "The shared copy is removed", gone ? "removed" : "still there"));
            }
            finally { ShareSheet.Intercept = null; }
        }

        /// <summary>Back in the Library: the exports just made are listed for the game. Then the temp folder goes.</summary>
        private async Task LibraryExportsCheck(string id)
        {
            if (_exportDir is not { } dir) return;
            var made = Directory.EnumerateFiles(dir, "*.mp4").Count();
            var listed = await WaitFor(() => Find("LibraryExportList") is { ActualHeight: > 0 }, 5);
            Add(CheckResult.From(listed && made > 0, $"{id}.library-exports", "Library lists the exports of this game", $"{made} files made, list {(listed ? "shown" : "not shown")}"));
            AppServices.Settings.ExportFolder = _savedExportFolder;
            _exportDir = null;
            try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        private void Press(string automationId)
        {
            if (Find(automationId) is { } e and not Control { IsEnabled: false }) Invoke(e);
        }

        private string Text(string automationId) => Find(automationId) is TextBlock t ? t.Text : "";

        private static double ParseDelay(string text)
        {
            var digits = new string([.. text.Where(c => char.IsDigit(c) || c == '-' || c == '+')]);
            return double.TryParse(digits, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms / 1000 : double.NaN;
        }

        /// <summary>Per stream: start and end in seconds.</summary>
        private static async Task<Dictionary<string, (double Start, double End)>> Streams(string path)
        {
            var r = await FastEncoder.Core.Ff.RunAsync(FastEncoder.Core.Ff.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type,start_time,duration", "-of", "csv=p=0", path]);
            var result = new Dictionary<string, (double, double)>();
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = line.Split(',');
                if (p.Length >= 3 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var start) && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var length))
                    result[p[0]] = (start, start + length);
            }
            return result;
        }

        private async Task ExportButtonCheck(string id, string name, string automationId, string title, string dir, double minSeconds, double maxSeconds, bool audio = true, long? maxBytes = null, string? suffix = null)
        {
            var before = Directory.EnumerateFiles(dir, "*.mp4").ToHashSet();
            var button = Find(automationId);
            if (button is null || !Invoke(button))
            {
                Add(CheckResult.Fail($"{id}.{name}", title, $"no {automationId} element"));
                return;
            }
            await WaitFor(() => AppServices.Exports.Pending == 0 && Directory.EnumerateFiles(dir, "*.mp4").Any(f => !before.Contains(f)), 60);
            var output = Directory.EnumerateFiles(dir, "*.mp4").FirstOrDefault(f => !before.Contains(f));
            var info = output is null ? null : await FastEncoder.Core.Ff.ProbeAsync(output);
            var bytes = output is null ? 0 : new FileInfo(output).Length;
            var ok = info is not null && info.HasAudio == audio && info.Duration >= minSeconds && info.Duration <= maxSeconds && (maxBytes is null || bytes <= maxBytes);
            var state = AppServices.Exports.Items.LastOrDefault()?.State.ToString() ?? "none";
            var detail = info is null ? $"no output (queue item: {state})" : $"{F(info.Duration, "0.00")} s, audio {info.HasAudio}, {bytes / 1024} KiB";
            if (info is not null && audio)
            {
                var s = await Streams(output!);
                var together = s.TryGetValue("video", out var v) && s.TryGetValue("audio", out var a) && Math.Abs(v.Start - a.Start) <= 0.12 && Math.Abs(v.End - a.End) <= 0.12;
                ok &= together;
                detail += together ? ", sound covers the picture" : ", SOUND AND PICTURE DIFFER: " + string.Join(" ", s.Select(kv => $"{kv.Key} {F(kv.Value.Start, "0.00")}..{F(kv.Value.End, "0.00")}"));
            }
            if (output is not null && suffix is not null)
            {
                var named = suffix.Length > 0 ? Path.GetFileNameWithoutExtension(output).EndsWith(suffix, StringComparison.Ordinal) : !Regex.IsMatch(Path.GetFileNameWithoutExtension(output), "_(UQ|Q|M|P|UP)$");
                ok &= named;
                if (!named) detail += ", wrong file name";
            }
            Add(CheckResult.From(ok, $"{id}.{name}", title, detail));
        }

        /// <summary>The transport controls (the volume slider or, when narrow, its button) and the timeline's zoom group.</summary>
        private static readonly string[][] TransportControls =
        [
            ["PlayerPlayPause"], ["PlayerBack5"], ["PlayerFrameBack"], ["PlayerFrameForward"], ["PlayerForward5"], ["PlayerTimecode"],
            ["PlayerAbLoop"], ["PlayerSpeed"], ["PlayerMute"], ["PlayerVolume", "PlayerVolumeButton"],
            ["TimelineZoomOut"], ["TimelineZoomLevel"], ["TimelineZoomIn"], ["PlayerFullscreen"],
        ];

        /// <summary>
        /// P3: every transport control lies fully inside the window and inside the player's lower block (nothing is clipped or pushed out) at
        /// the default and at the minimum window size.
        /// </summary>
        private async Task TransportBounds(string id)
        {
            var aw = window.AppWindow;
            var original = aw.Size;
            var op = aw.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            var minW = Math.Max(op?.PreferredMinimumWidth ?? 0, 1);
            var minH = Math.Max(op?.PreferredMinimumHeight ?? 0, 1);
            try
            {
                await TransportBoundsAt(id, "default", original.Width, original.Height);
                aw.Resize(new Windows.Graphics.SizeInt32(minW, minH));
                await Task.Delay(500);
                await TransportBoundsAt(id, "minimum", minW, minH);
                await Capture(id + "-min");
                await ResizeStress(id);
            }
            finally
            {
                aw.Resize(original);
                await Task.Delay(300);
                await WaitFor(() => Find("PlayerExport") is Button { IsEnabled: true }, 30);        // encoder detection may still be running
            }
        }

        /// <summary>
        /// The frame-back button as a person presses it, read from the screen: each press moves one frame, FF stays a frame number of the
        /// stream's real rate, and the duration field never changes (a wrong rate doubled FF and changed the duration's frames).
        /// </summary>
        private async Task FrameBackButton(string id)
        {
            var s = Session()!;
            var mpv = PlayerHost.Instance!.Mpv!;
            var fps = ChunkFrameRate.Measure(s.Media!) ?? 60;
            var back = Find("PlayerFrameBack");
            if (back is null) { Add(CheckResult.Fail($"{id}.frame-back-button", "Frame-back button moves one frame per press", "no PlayerFrameBack element")); return; }
            await WaitFor(() => mpv.TimePos > s.MediaStartSeconds + 2 || s.UiDuration <= 2, 4);
            // the first press pauses the playing video, so the count starts after it
            Invoke(back);
            await WaitFor(() => s.UserPaused && !s.IsReloading, 3);
            await Task.Delay(250);
            var before = mpv.TimePos;
            const int presses = 8;
            var texts = new List<string>();
            for (var i = 0; i < presses; i++)
            {
                Invoke(back);
                await Task.Delay(180);
                texts.Add((Find("PlayerTimecode") as TextBlock)?.Text ?? "");
            }
            await WaitFor(() => !s.IsReloading, 3);
            await Task.Delay(250);
            texts.Add((Find("PlayerTimecode") as TextBlock)?.Text ?? "");
            var moved = (before - mpv.TimePos) * fps;
            var parts = texts.Select(t => t.Split(" / ")).Where(p => p.Length == 2).ToList();
            var durations = parts.Select(p => p[1]).Distinct().Count();
            var maxFf = parts.Max(p => int.Parse(p[0][^2..], CultureInfo.InvariantCulture));
            var live = s.Media!.Live;                                  // a live recording grows while the check runs, so its length may change
            Add(CheckResult.From(parts.Count == texts.Count && (live || durations == 1) && maxFf <= Math.Ceiling(fps) && moved is > presses - 1.5 and < presses + 1.5,
                $"{id}.frame-back-button", "Frame-back button: one frame per press, stable timecode",
                $"moved {F(moved, "0.0")} frames in {presses} presses, highest FF {maxFf} at {F(fps, "0.##")} fps, duration {string.Join(" then ", parts.Select(p => p[1]).Distinct())}, session {F(s.UiDuration, "0.000")} s"));
            await FrameBackAcrossChunks(id, s, mpv, () => Invoke(back), fps);
        }

        /// <summary>
        /// The press rate a person holds the button at, far enough to cross two chunks: every press has to move a frame, also at each
        /// chunk's first frame, where the player has to load and walk the chunk before.
        /// </summary>
        private async Task FrameBackAcrossChunks(string id, PlaybackSession s, MpvPlayer mpv, Action press, double fps)
        {
            var media = s.Media!;
            var chunk = media.Chunks().Where(c => media.ChunkStartSeconds(c) - 2 * media.SegmentSeconds - 1 >= media.StartSeconds && media.ChunkEndSeconds(c) <= mpv.TimePos).LastOrDefault();
            if (chunk == 0) { Add(CheckResult.Fail($"{id}.frame-back-chunks", "Frame back keeps moving across two chunks", "the recording is too short to cross two chunks")); return; }
            await s.SeekAsync(media.ChunkStartSeconds(chunk) + 0.3);
            await WaitFor(() => s.UserPaused && !s.IsReloading, 3);
            await Task.Delay(250);
            var before = mpv.TimePos;
            var presses = (int)Math.Ceiling((media.SegmentSeconds + 0.5) * fps);
            for (var i = 0; i < presses; i++)
            {
                press();
                await Task.Delay(180);
            }
            await WaitFor(() => !s.IsReloading, 5);
            await Task.Delay(250);
            var after = mpv.TimePos;
            var moved = await FramesBetween(media, chunk, after, before);
            Add(CheckResult.From(Math.Abs(moved - presses) <= 1, $"{id}.frame-back-chunks", "Frame back keeps moving across two chunks at 180 ms a press",
                $"moved {moved} real frames in {presses} presses, {F(before - after, "0.000")} s back; back-steps {s.BackStepCounts}"));
        }

        /// <summary>Stored frames with a pts in (<paramref name="after"/>, <paramref name="before"/>]: counted, because Steam's frame rate varies and seconds × fps isn't a frame count.</summary>
        private static async Task<int> FramesBetween(SessionMedia media, int lastChunk, double after, double before)
        {
            var path = Path.Combine(Path.GetTempPath(), "spp-uicheck-" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
            try
            {
                await using (var source = Core.Export.RangeSource.Create(media, 0, Math.Max(media.FirstChunk!.Value, lastChunk - 3), lastChunk))
                await using (var target = File.Create(path))
                    await source.CopyToAsync(target);
                var r = await FastEncoder.Core.Ff.RunAsync(FastEncoder.Core.Ff.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path]);
                return r.StdOut.Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => double.Parse(l.TrimEnd(','), CultureInfo.InvariantCulture))
                    .Count(t => t > after + 0.002 && t <= before + 0.002);
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        }

        /// <summary>
        /// A border drag while the video plays: the width sweeps back and forth across the layout thresholds in irregular steps, one layout
        /// pass apart. A XAML fail-fast ends the process, and the parent reports it as a crash.
        /// </summary>
        private async Task ResizeStress(string id)
        {
            var s = Page() is PlayerPage ? Session() : null;
            var wasPaused = s?.UserPaused ?? false;
            s?.SetPause(false);
            var aw = window.AppWindow;
            var original = aw.Size;
            var op = aw.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            var minW = Math.Max(op?.PreferredMinimumWidth ?? 0, 1);
            var minH = Math.Max(op?.PreferredMinimumHeight ?? 0, 1);
            var maxW = Math.Max(Math.Max(original.Width, _defaultWidth), minW * 3 / 2);
            var h = Math.Max(minH, _defaultHeight > 0 ? _defaultHeight : original.Height);
            var random = new Random(7);
            var (w, direction, steps) = (maxW, -1, 0);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(8))
            {
                w = Math.Clamp(w + direction * random.Next(4, 90), minW, maxW);
                if (w == minW || w == maxW || random.Next(12) == 0) direction = -direction;
                aw.Resize(new Windows.Graphics.SizeInt32(w, h));
                steps++;
                await Task.Delay(random.Next(0, 24));
            }
            s?.SetPause(wasPaused);
            aw.Resize(original);
            await Task.Delay(300);
            Add(CheckResult.Pass($"{id}.resize-stress", "Sweeping the window width does not crash", $"{steps} resizes between {minW} and {maxW} px"));
        }

        /// <summary>
        /// P4: the timeline ruler is present, has labels, and its labels neither overlap nor leave the ruler at 1x and at the deepest zoom
        /// (the + button, repeatedly). Bounds come from the elements themselves, not from the layout maths.
        /// </summary>
        private async Task RulerChecks(string id)
        {
            var zoomOut = Find("TimelineZoomOut");
            var zoomIn = Find("TimelineZoomIn");
            if (zoomOut is null || zoomIn is null || Find("TimelineRuler") is not Canvas) { Add(CheckResult.Fail($"{id}.ruler", "Timeline ruler present", "no TimelineRuler canvas or zoom buttons")); return; }
            for (var i = 0; i < 30 && zoomOut is Control { IsEnabled: true }; i++) Invoke(zoomOut);
            await Task.Delay(300);
            await RulerAt(id, "1x");
            await Capture(id + "-ruler-1x");
            for (var i = 0; i < 30 && zoomIn is Control { IsEnabled: true }; i++) Invoke(zoomIn);
            await Task.Delay(300);
            await RulerAt(id, "max zoom");
            await Capture(id + "-ruler-max");
            for (var i = 0; i < 30 && zoomOut is Control { IsEnabled: true }; i++) Invoke(zoomOut);
            await Task.Delay(300);
        }

        private async Task RulerAt(string id, string label)
        {
            await Task.Delay(100);
            if (Find("TimelineRuler") is not Canvas ruler || window.Content is not FrameworkElement root) { Add(CheckResult.Fail($"{id}.ruler-{label}", $"Ruler labels ({label})", "no TimelineRuler canvas")); return; }
            var to = ruler.TransformToVisual(root);
            var area = to.TransformBounds(new Windows.Foundation.Rect(0, 0, ruler.ActualWidth, ruler.ActualHeight));
            var labels = ruler.Children.OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible && !string.IsNullOrEmpty(t.Text)).ToList();
            var rects = labels.Select(t => (t.Text, B: t.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, t.ActualWidth, t.ActualHeight))))
                .OrderBy(r => r.B.Left).ToList();
            var problems = new List<string>();
            double minGap = double.MaxValue;
            for (var i = 1; i < rects.Count; i++)
            {
                var gap = rects[i].B.Left - rects[i - 1].B.Right;
                minGap = Math.Min(minGap, gap);
                if (gap <= 0) problems.Add($"\"{rects[i - 1].Text}\" and \"{rects[i].Text}\" overlap ({gap:0.0} px)");
            }
            // A label may slide in from the left edge; the right edge is clipped by the ruler's own clip, so only require that it starts inside.
            foreach (var r in rects) if (r.B.Left > area.Right + 1 || r.B.Top < area.Top - 1 || r.B.Bottom > area.Bottom + 1) problems.Add($"\"{r.Text}\" outside the ruler");
            var shown = ruler.ActualWidth > 0 && ruler.ActualHeight > 0;
            var sample = string.Join(" ", rects.Take(4).Select(r => r.Text));
            var ok = shown && rects.Count >= 2 && problems.Count == 0;
            Add(CheckResult.From(ok, $"{id}.ruler-{label.Replace(' ', '-')}", $"Ruler shown, labels do not overlap ({label})",
                $"{(shown ? $"{ruler.ActualWidth:0}x{ruler.ActualHeight:0} DIP" : "ruler has no size")}, {rects.Count} labels, min gap {(rects.Count > 1 ? minGap.ToString("0.0", CultureInfo.InvariantCulture) : "-")} px, e.g. {sample}"
                + (problems.Count > 0 ? "; " + string.Join("; ", problems.Take(3)) : "")));
        }

        private async Task TransportBoundsAt(string id, string label, int pixelsW, int pixelsH)
        {
            await Task.Delay(300);
            var root = window.Content as FrameworkElement;
            var lower = Find("PlayerLower");
            if (root is null || lower is null) { Add(CheckResult.Fail($"{id}.transport-{label}", $"Transport controls fully visible ({label} window)", "no PlayerLower element")); return; }
            const double eps = 1.0;
            var area = new Windows.Foundation.Rect(0, 0, root.ActualWidth, root.ActualHeight);
            var lowerBounds = lower.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, lower.ActualWidth, lower.ActualHeight));
            var bad = new List<string>();
            var missing = new List<string>();
            foreach (var ids in TransportControls)
            {
                var el = ids.Select(Find).FirstOrDefault(e => e is { ActualWidth: > 0 });
                if (el is null) { missing.Add(ids[0]); continue; }
                var b = el.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, el.ActualWidth, el.ActualHeight));
                var inWindow = b.Left >= -eps && b.Top >= -eps && b.Right <= area.Right + eps && b.Bottom <= area.Bottom + eps;
                var inLower = b.Left >= lowerBounds.Left - eps && b.Right <= lowerBounds.Right + eps;
                if (!inWindow || !inLower)
                    bad.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{AutomationProperties.GetAutomationId(el)} x {b.Left:0}..{b.Right:0} (lower {lowerBounds.Left:0}..{lowerBounds.Right:0}, window 0..{area.Right:0}) y {b.Top:0}..{b.Bottom:0} (window height {area.Bottom:0})"));
            }
            var ok = bad.Count == 0 && missing.Count == 0;
            Add(CheckResult.From(ok, $"{id}.transport-{label}", $"Every transport control fully visible ({label} window, {pixelsW}x{pixelsH} px)",
                ok ? $"{TransportControls.Length} controls inside the window and the player block, content {area.Right:0}x{area.Bottom:0} DIP"
                   : (missing.Count > 0 ? "not shown: " + string.Join(", ", missing) + "; " : "") + string.Join("; ", bad)));
        }

        private async Task Back(string id)
        {
            var back = Find("PlayerBack");
            var invoked = back is not null && Invoke(back);
            var ok = await WaitFor(() => Page() is LibraryPage, 5);
            await Task.Delay(500);           // a late crash in the old page's teardown or the library's resume shows up here
            ok &= Page() is LibraryPage;
            Add(CheckResult.From(invoked && ok, $"{id}.back", "Back < returns to the library",
                !invoked ? "no PlayerBack element" : ok ? "library page shown" : $"page {PageName()}"));
            await Capture(id + "-back");
        }

        private async Task Nav(string automationId, Type page, string id, string name)
        {
            var item = Find(automationId);
            var selected = item is not null && Select(item);
            var ok = await WaitFor(() => Page()?.GetType() == page, 5);
            await Task.Delay(500);
            ok &= Page()?.GetType() == page;
            Add(CheckResult.From(selected && ok, id, name, !selected ? $"no selectable {automationId}" : ok ? page.Name + " shown" : $"page {PageName()}"));
        }

        // ---- the tree -----------------------------------------------------------------------------------------------

        [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}\.\d{2} / \d{2}:\d{2}:\d{2}\.\d{2}$")]
        private static partial Regex Timecode();

        private static PlaybackSession? Session() => PlayerHost.Instance?.Session;

        private Frame? ContentFrame() => Descendants(window.Content).OfType<Frame>().FirstOrDefault();
        private object? Page() => ContentFrame()?.Content;
        private string PageName() => Page()?.GetType().Name ?? "none";

        private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
        {
            if (root is null) yield break;
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                yield return d;
                var n = VisualTreeHelper.GetChildrenCount(d);
                for (var i = n - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
            }
        }

        private FrameworkElement? Find(string automationId) =>
            Descendants(window.Content).OfType<FrameworkElement>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == automationId && e.Visibility == Visibility.Visible);

        private FrameworkElement? FindByIdAndTag(string automationId, Func<object?, bool> tag) =>
            Descendants(window.Content).OfType<FrameworkElement>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == automationId && tag(e.Tag));

        private FrameworkElement? FindClipTile(string relativeFolder)
        {
            bool Match(object? t) => t is ClipTile c && c.Key == relativeFolder;
            if (FindByIdAndTag("LibraryClipTile", Match) is { } realized) return realized;
            // the clip grid is virtualized: realize the tile's element
            foreach (var r in Descendants(window.Content).OfType<ItemsRepeater>())
            {
                if (r.ItemsSource is not IList list) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    if (!Match(list[i])) continue;
                    var el = r.GetOrCreateElement(i);
                    el.StartBringIntoView();
                    r.UpdateLayout();
                    return el as FrameworkElement is { } fe && AutomationProperties.GetAutomationId(fe) == "LibraryClipTile" ? fe
                        : Descendants(el).OfType<FrameworkElement>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == "LibraryClipTile");
                }
            }
            return null;
        }

        private void SelectGame(string? gameId)
        {
            if (Find("LibraryGameList") is not ListView list) return;
            var tile = list.Items.OfType<GameTile>().FirstOrDefault(t => t.GameId == gameId);
            if (tile is null || ReferenceEquals(list.SelectedItem, tile)) return;
            list.ScrollIntoView(tile);
            list.UpdateLayout();
            if (list.ContainerFromItem(tile) is ListViewItem item && Select(item)) return;
            list.SelectedItem = tile;
        }

        private static AutomationPeer? Peer(UIElement e) => FrameworkElementAutomationPeer.FromElement(e) ?? FrameworkElementAutomationPeer.CreatePeerForElement(e);

        /// <summary>UIA Invoke (buttons) or Toggle (toggle buttons) through the element's automation peer.</summary>
        private static bool Invoke(UIElement e)
        {
            if (Peer(e) is not { } p) return false;
            if (p.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv) { inv.Invoke(); return true; }
            if (p.GetPattern(PatternInterface.Toggle) is IToggleProvider tog) { tog.Toggle(); return true; }
            return false;
        }

        private static bool Select(UIElement e)
        {
            if (Peer(e)?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel) { sel.Select(); return true; }
            return false;
        }

        private static async Task<bool> WaitFor(Func<bool> condition, double seconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (condition()) return true;
                await Task.Delay(50);
            }
            return condition();
        }

        // ---- captures (PrintWindow with full content, so the video swap chain is included) ---------------------------

        private async Task Capture(string label)
        {
            await Task.Delay(250);
            if (request.Captures is not { } dir) return;
            try
            {
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"uicheck-{_segment ?? "all"}-{++_capture:00}-{label}.bmp"));
                WindowCapture.Save(window.Hwnd, file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
    }

    /// <summary>F10: samples the title-bar chips every 250 ms; a chip whose width changes while the set of chips stays the same is a FAIL.</summary>
    private sealed class ChipWatch
    {
        private DispatcherQueueTimer? _timer;
        private double[] _last = [];
        private int _samples, _changes;
        private string? _example;

        public void Start(MainWindow window)
        {
            _timer = window.DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += (_, _) => Sample(window);
            _timer.Start();
        }

        public void Stop() => _timer?.Stop();

        private void Sample(MainWindow window)
        {
            var panel = FindPanel(window.Content);
            if (panel is null) return;
            var widths = panel.Children.OfType<FrameworkElement>().Select(c => Math.Round(c.ActualWidth, 1)).ToArray();
            _samples++;
            if (widths.Length == _last.Length)
                for (var i = 0; i < widths.Length; i++)
                    if (widths[i] > 0 && _last[i] > 0 && Math.Abs(widths[i] - _last[i]) > 0.5)      // 0 = not laid out yet
                    {
                        _changes++;
                        _example ??= string.Create(CultureInfo.InvariantCulture, $"chip {i + 1}: {_last[i]:0.#} -> {widths[i]:0.#} px");
                    }
            _last = widths;
        }

        private static Panel? FindPanel(DependencyObject? root)
        {
            if (root is null) return null;
            if (root is Panel p && AutomationProperties.GetAutomationId(p) == "TitleChips") return p;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindPanel(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }

        public CheckResult Result() =>
            _samples == 0
                ? CheckResult.Fail("shell.chips", "Title-bar chips keep their width (F10)", "no TitleChips element")
                : CheckResult.From(_changes == 0, "shell.chips", "Title-bar chips keep their width (F10)",
                    $"{_samples} samples, {_changes} width change(s)" + (_example is null ? "" : ", e.g. " + _example));
    }
}

/// <summary>Saves a window as a 32-bit BMP with <c>PrintWindow(PW_RENDERFULLCONTENT)</c> (includes DirectComposition content such as the video).</summary>
internal static partial class WindowCapture
{
    public static void Save(IntPtr hwnd, string file)
    {
        if (!GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("GetWindowRect failed");
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("empty window");
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            PrintWindow(hwnd, mem, 2);
            var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[w * h * 4];
            SelectObject(mem, old);
            if (GetDIBits(mem, bmp, 0, (uint)h, pixels, ref info, 0) == 0) throw new InvalidOperationException("GetDIBits failed");
            using var fs = File.Create(file);
            using var bw = new BinaryWriter(fs);
            bw.Write((ushort)0x4D42);
            bw.Write(54 + pixels.Length);
            bw.Write(0);
            bw.Write(54);
            bw.Write(40); bw.Write(w); bw.Write(-h); bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write(0); bw.Write(pixels.Length); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
            bw.Write(pixels);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        // BITMAPINFO's colour table is not used for 32-bit BI_RGB; a few spare bytes keep GetDIBits inside the struct
        public int pad0, pad1, pad2, pad3;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
}
