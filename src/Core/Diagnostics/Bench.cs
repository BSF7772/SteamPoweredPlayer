// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <param name="Library">A BackgroundRecordPath; null = <c>SPP_REAL_LIBRARY</c>, else Steam's configured one.</param>
/// <param name="Quick">The short version: one minute of playback, shorter CPU windows, at most 300 chunks of trickplay.</param>
public sealed record BenchOptions(string? Library = null, bool Quick = false);

/// <summary>
/// <c>SteamPoweredPlayer.exe --bench &lt;out.txt&gt; [--library &lt;BackgroundRecordPath&gt;] [--quick]</c>: the desktop-local budgets of plan Â§5
/// measured on a real library. <b>Read-only on the library</b>: the data folder (index, scan cache, art, trickplay sheets) is a temp
/// directory that is deleted at the end; the session files are only read (mpv, the scanner, <see cref="Export.RangeSource"/>).
/// Playback is headless libmpv (<c>vo=null</c>): the render path (D3D11 swap chain, XAML) is not included; the Player page measures
/// its own open and seek. The report holds hardware class and numbers only (no paths, names or ids).
/// </summary>
public static class Bench
{
    public static async Task<int> RunAsync(string reportPath, BenchOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        var report = new DiagnosticReport("bench", "SteamPoweredPlayer --bench" + (options.Quick ? " --quick" : "") + " (real library, read-only)");
        var sw = Stopwatch.StartNew();
        string? secret = null;
        try
        {
            var runner = new Runner(report, reportPath, options);
            secret = await runner.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            report.Add(CheckResult.Fail("bench.crash", "Bench crashed", "exception", Redactor.Describe(e, secret)));
            report.Abort("unexpected exception");
        }
        report.Add(CheckResult.Info("bench.elapsed", "Elapsed", BudgetRules.F(sw.Elapsed.TotalSeconds) + " s", number: sw.Elapsed.TotalSeconds, unit: "s"));
        report.WriteTo(reportPath);
        return report.ExitCode;
    }

    /// <summary>The library to measure: the explicit argument, <c>SPP_REAL_LIBRARY</c>, then Steam's <c>BackgroundRecordPath</c>. Source is a label, never the path.</summary>
    internal static async Task<(string? Root, string Source, SteamPaths? Paths, uint? Account)> ResolveLibraryAsync(string? argument, CancellationToken ct)
    {
        SteamPaths? paths = null;
        uint? account = null;
        string? steamRoot = null;
        try
        {
            paths = SteamPaths.FromRegistry();
            if (paths is not null && await paths.ReadActiveUserAsync(ct).ConfigureAwait(false) is { } user)
            {
                account = user.AccountId;
                var cfg = await SteamConfig.LoadAsync(paths.LocalConfig(user.AccountId), ct).ConfigureAwait(false);
                steamRoot = cfg.BackgroundRecordPath;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }

        if (!string.IsNullOrWhiteSpace(argument))
            return Directory.Exists(argument) ? (argument, "--library", paths, account) : (null, "--library (folder not found)", paths, account);
        var env = Environment.GetEnvironmentVariable("SPP_REAL_LIBRARY");
        if (!string.IsNullOrWhiteSpace(env))
            return Directory.Exists(env) ? (env, "SPP_REAL_LIBRARY", paths, account) : (null, "SPP_REAL_LIBRARY (folder not found)", paths, account);
        if (!string.IsNullOrWhiteSpace(steamRoot))
            return Directory.Exists(steamRoot) ? (steamRoot, "Steam's BackgroundRecordPath", paths, account) : (null, "Steam's BackgroundRecordPath (folder not found)", paths, account);
        return (null, "none (Steam not found)", paths, account);
    }

    private sealed class Runner(DiagnosticReport report, string reportPath, BenchOptions options)
    {
        private string _root = "";
        private string _temp = "";
        private SessionMedia _media = null!;
        private double _start, _end;
        private DecodeMode _primary = DecodeMode.Cpu;
        private bool _gpu;
        private bool _renderHidden = true;
        private int _cores;

        private TimeSpan SoakDuration => options.Quick ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5);
        private TimeSpan CpuWarm => TimeSpan.FromSeconds(3);
        private TimeSpan CpuWindow => options.Quick ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);
        private int OpenRuns => options.Quick ? 5 : 10;
        private int SeekCount => options.Quick ? 30 : 60;
        private static string F(double v, string f = "0.#") => BudgetRules.F(v, f);
        private string S(string? text) => Redactor.Scrub(text, _root);

        private void Add(CheckResult c)
        {
            report.Add(c);
            report.WriteTo(reportPath);
        }

        private async Task Step(string id, string name, Func<Task> body)
        {
            try { await body().ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Add(CheckResult.Fail(id, name, "exception", Redactor.Describe(e, _root)));
            }
        }

        public async Task<string?> RunAsync(CancellationToken ct)
        {
            var hw = HardwareClass.Detect();
            _cores = hw.LogicalCores;
            report.AddHeader("Hardware: " + hw.Describe());
            report.AddHeader("Mode: " + (options.Quick ? "quick (1 min playback soak)" : "full (5 min playback soak)"));
            report.AddHeader("Method: libmpv without a visible window (audio off). The render path of the app (XAML swap chain) is not included; the Player page measures its own open and seek. "
                + "Machine load was not controlled. The library is only read; the data folder is a deleted temp directory.");

            var (root, source, paths, account) = await ResolveLibraryAsync(options.Library, ct).ConfigureAwait(false);
            report.AddHeader("Library source: " + source);
            if (root is null)
            {
                Add(CheckResult.Fail("bench.library", "Library found", source));
                report.Abort("no library to measure; pass --library <BackgroundRecordPath> or set SPP_REAL_LIBRARY");
                return null;
            }
            _root = Path.GetFullPath(root);

            Ff.Locate();
            if (!Ff.Available)
            {
                Add(CheckResult.Fail("env.ffmpeg", "ffmpeg and ffprobe found", "not found"));
                report.Abort("ffmpeg is required");
                return _root;
            }
            DiagnosticsEnvironment.PrepareMpv();
            if (!MpvLibrary.IsAvailable())
            {
                Add(CheckResult.Fail("env.libmpv", "libmpv loads", "libmpv-2.dll not found"));
                report.Abort("libmpv is required");
                return _root;
            }

            _temp = Path.Combine(Path.GetTempPath(), "spp-bench-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_temp);
            try
            {
                var svc = await LibrarySection(paths, account, ct).ConfigureAwait(false);
                if (svc is null) return _root;
                try
                {
                    if (!await PickSession(svc, ct).ConfigureAwait(false)) return _root;
                }
                finally { await svc.DisposeAsync().ConfigureAwait(false); }

                await DetectHardwareDecode(ct).ConfigureAwait(false);
                // RAM first: the process is still close to idle, so the "before playback" figure is the floor and not the residue of the other sections.
                await Step("bench.ram", "RAM", () => RamSection(ct)).ConfigureAwait(false);
                await Step("bench.open", "Open", () => OpenSection(ct)).ConfigureAwait(false);
                await Step("bench.seek", "Seek", () => SeekSection(ct)).ConfigureAwait(false);
                await Step("bench.export", "Export", () => ExportSection(ct)).ConfigureAwait(false);
                await Step("bench.trickplay", "Trickplay", () => TrickplaySection(ct)).ConfigureAwait(false);
                await Step("bench.cpu", "CPU", () => CpuSection(ct)).ConfigureAwait(false);
            }
            finally
            {
                try { Directory.Delete(_temp, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return _root;
        }

        // ---- library -------------------------------------------------------------------------------------------------

        private async Task<LibraryService?> LibrarySection(SteamPaths? paths, uint? account, CancellationToken ct)
        {
            var data = DataFolder.Open(Path.Combine(_temp, "data"), _root);
            data.EnsureCreated();
            GameNameResolver? names = paths is null ? null : new GameNameResolver(paths, account);
            LibraryService NewService() => new(new LibraryServiceOptions { RecordingRoot = _root, DataFolder = data, Names = names });

            // cold: nothing saved, every folder is scanned
            var cold = NewService();
            var sw = Stopwatch.StartNew();
            await cold.StartAsync(ct).ConfigureAwait(false);
            await cold.WhenIdleAsync(ct).ConfigureAwait(false);
            var coldMs = sw.Elapsed.TotalMilliseconds;
            var recordings = cold.Index.Recordings.Count;
            var chunks = cold.Index.Recordings.Sum(r => r.Runs.Sum(x => x.Count));
            await cold.FlushAsync(ct).ConfigureAwait(false);
            await cold.DisposeAsync().ConfigureAwait(false);
            Add(CheckResult.Info("bench.library.cold", "Library cold scan (no index yet)", $"{F(coldMs)} ms for {recordings} recordings, {chunks} chunks",
                number: coldMs, unit: "ms"));
            if (recordings == 0)
            {
                Add(CheckResult.Fail("bench.library.warm", "Library visible from index (warm)", "the library is empty"));
                report.Abort("the library has no recordings");
                return null;
            }

            // warm: the saved index is raised first, before anything is read from the recording folder
            var warm = NewService();
            var visible = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wsw = Stopwatch.StartNew();
            warm.IndexChanged += (idx, _) =>
            {
                if (idx.Recordings.Count > 0) visible.TrySetResult(wsw.Elapsed.TotalMilliseconds);
            };
            await warm.StartAsync(ct).ConfigureAwait(false);
            var warmMs = await visible.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            await warm.WhenIdleAsync(ct).ConfigureAwait(false);
            var warmIdle = wsw.Elapsed.TotalMilliseconds;
            Add(BudgetRules.LibraryVisible(warmMs));
            Add(CheckResult.Info("bench.library.warm-scan", "Library warm rescan finished (heads remembered)", F(warmIdle) + " ms", number: warmIdle, unit: "ms"));
            return warm;
        }

        private async Task<bool> PickSession(LibraryService svc, CancellationToken ct)
        {
            var best = svc.Index.Recordings
                .Where(r => !r.Live && r.Runs.Count > 0)
                .OrderByDescending(r => r.Runs.Sum(x => x.Count))
                .FirstOrDefault();
            var media = best is null ? null : svc.GetMedia(best.RelativeFolder);
            if (best is null || media is null)
            {
                Add(CheckResult.Fail("bench.session", "A playable session to measure", "none found"));
                report.Abort("no playable, finished session");
                return false;
            }
            // fresh scan: Steam may have trimmed since the index was made
            var rescan = await Scanner.ScanAsync(media.Folder, new ScanOptions { Temporary = media.Temporary }, ct).ConfigureAwait(false);
            _media = rescan.Media ?? media;
            _start = _media.ChunkStartSeconds(_media.FirstChunk!.Value);
            _end = _media.LastChunk!.Value * _media.SegmentSeconds;
            report.AddHeader(string.Create(CultureInfo.InvariantCulture,
                $"Session measured: the longest finished one, {_media.ChunkCount} chunks ({F(_media.DurationSeconds / 60, "0.0")} min), {_media.Runs.Count} run(s), {best.Kind}"));
            return true;
        }

        // ---- decode mode -----------------------------------------------------------------------------------------------

        private async Task DetectHardwareDecode(CancellationToken ct)
        {
            // 1) hwdec=d3d11va into mpv's own D3D11 renderer on a hidden window (zero-copy, what the app does);
            // 2) hwdec=d3d11va-copy with vo=null (copy-back: slower, but needs no window); 3) software.
            string? why = null;
            foreach (var hidden in new[] { true, false })
            {
                try
                {
                    await using var rig = PlaybackRig.Create(DecodeMode.Gpu, renderHidden: hidden);
                    if (hidden && rig.Wid is null) { why = "no window could be created"; continue; }
                    await rig.Session.OpenAsync(_media, Mid(0.5), play: true, ct).ConfigureAwait(false);
                    await Task.Delay(400, ct).ConfigureAwait(false);
                    if (!rig.UsesHardware) { why = "mpv decoded in software"; continue; }
                    _gpu = true;
                    _renderHidden = hidden;
                    Add(CheckResult.Info("bench.hwdec", "Hardware decode", $"hwdec-current {rig.HwdecCurrent}, "
                        + (hidden ? "zero-copy into mpv's D3D11 renderer on a hidden window" : "copy-back with vo=null (the zero-copy path was not available: " + why + ")")));
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    why = "probe failed: " + S(e.Message);
                }
            }
            if (!_gpu)
            {
                _renderHidden = false;
                Add(CheckResult.Skip("bench.hwdec", "Hardware decode", "not available (" + why + "); budgets are measured with CPU decode"));
            }
            _primary = _gpu ? DecodeMode.Gpu : DecodeMode.Cpu;
            report.AddHeader("Decoder for the budgets: " + (_gpu ? (_renderHidden ? "d3d11va (hardware, zero-copy)" : "d3d11va-copy (hardware, copy-back)") : "software (no hardware decode available)"));
            report.AddHeader("Video output: " + (_renderHidden ? "mpv vo=gpu (D3D11) on a hidden 1280x720 window" : "vo=null (nothing is rendered)"));
        }

        private double Mid(double fraction) => PlaybackSession.Snap(_media, _start + (_end - _start) * fraction);

        // ---- open -> first frame ---------------------------------------------------------------------------------------

        private async Task OpenSection(CancellationToken ct)
        {
            var warm = new List<double>();
            var fresh = new List<double>();
            for (var i = 0; i < OpenRuns; i++)
            {
                await using (var rig = PlaybackRig.Create(_primary, renderHidden: _renderHidden))
                {
                    if (!await rig.PreWarmAsync(_media, ct).ConfigureAwait(false)) throw new TimeoutException("the pre-warm load did not deliver a frame");
                    var sw = Stopwatch.StartNew();
                    await rig.Session.OpenAsync(_media, null, play: true, ct).ConfigureAwait(false);
                    warm.Add(sw.Elapsed.TotalMilliseconds);
                }
                await using (var rig = PlaybackRig.Create(_primary, renderHidden: _renderHidden))
                {
                    var sw = Stopwatch.StartNew();
                    await rig.Session.OpenAsync(_media, null, play: true, ct).ConfigureAwait(false);
                    fresh.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            var wm = BudgetRules.Median(warm);
            var line = BudgetRules.FirstFrame("bench.open.prewarmed", "Open to first frame, pre-warmed (median)", wm);
            Add(line with { Value = $"{F(wm)} ms (max {F(warm.Max())}, {warm.Count} runs)" });
            var fm = BudgetRules.Median(fresh);
            Add(CheckResult.Info("bench.open.fresh", "Open to first frame, fresh mpv (median)", $"{F(fm)} ms (max {F(fresh.Max())}, {fresh.Count} runs)", "needs the pre-warm when above the budget", fm, "ms"));
        }

        // ---- seek ------------------------------------------------------------------------------------------------------

        private IReadOnlyList<double> SeekTargets()
        {
            var rng = new Random(20261107);
            var targets = new List<double>();
            var span = Math.Max(1, _end - _start - 1);
            var last = _start;
            for (var i = 0; i < SeekCount; i++)
            {
                // half jump anywhere (across many chunks), half scrub near the last target, forward and backward
                var t = i % 2 == 0 ? _start + rng.NextDouble() * span : last + (rng.NextDouble() - 0.5) * 60;
                last = Math.Clamp(t, _start, _end - 1);
                targets.Add(last);
            }
            return targets;
        }

        private async Task<List<double>> TimeSeeks(DecodeMode mode, CancellationToken ct)
        {
            await using var rig = PlaybackRig.Create(mode, renderHidden: _renderHidden);
            await rig.PreWarmAsync(_media, ct).ConfigureAwait(false);
            await rig.Session.OpenAsync(_media, null, play: false, ct).ConfigureAwait(false);
            var times = new List<double>();
            var sw = new Stopwatch();
            foreach (var t in SeekTargets())
            {
                sw.Restart();
                await rig.Session.SeekAsync(t, ct).ConfigureAwait(false);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            return times;
        }

        private async Task SeekSection(CancellationToken ct)
        {
            var primary = BudgetRules.Seek(await TimeSeeks(_primary, ct).ConfigureAwait(false));
            var label = _gpu ? "hardware decode" : "CPU decode";
            Add(BudgetRules.SeekGate("bench.seek", $"Seek by reload, {label}", primary));
            if (_gpu)
            {
                var cpu = BudgetRules.Seek(await TimeSeeks(DecodeMode.Cpu, ct).ConfigureAwait(false));
                Add(CheckResult.Info("bench.seek.cpu", "Seek by reload, CPU decode (not gated)",
                    $"median {F(cpu.MedianMs)}, p90 {F(cpu.P90Ms)}, max {F(cpu.MaxMs)} ms; {cpu.OverBudget}/{cpu.Count} over {F(Budgets.SeekMs)} ms", number: cpu.P90Ms, unit: "ms"));
            }
        }

        // ---- export ----------------------------------------------------------------------------------------------------

        private async Task ExportSection(CancellationToken ct)
        {
            var run = _media.Runs.OrderByDescending(r => r.Count).First();
            if (run.Count < 10)
            {
                Add(CheckResult.Skip("bench.export", "Lossless 30 s export", "the session has no run of 10 chunks"));
                return;
            }
            var first = run.First + (run.Count - 10) / 2;
            var scratch = Path.Combine(_temp, "export");
            Directory.CreateDirectory(scratch);
            var times = new List<double>();
            string? problem = null;
            for (var i = 0; i < 3; i++)
            {
                var outFile = Path.Combine(scratch, $"out{i.ToString(CultureInfo.InvariantCulture)}.mp4");
                var sw = Stopwatch.StartNew();
                var code = await ExportRange(first, first + 9, scratch, outFile, ct).ConfigureAwait(false);
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (code != 0) { problem = "ffmpeg exit code " + code.ToString(CultureInfo.InvariantCulture); break; }
                var dur = (await Ff.ProbeAsync(outFile, ct).ConfigureAwait(false))?.Duration ?? double.NaN;
                if (Math.Abs(dur - 30) > 0.5) { problem = $"output is {F(dur, "0.00")} s, expected 30 s"; break; }
            }
            if (problem is not null)
            {
                Add(CheckResult.Fail("bench.export", "Lossless 30 s export", problem));
                return;
            }
            var median = BudgetRules.Median(times);
            var c = BudgetRules.Export(median);
            Add(c with { Value = $"{F(median)} ms median of {times.Count} (runs {string.Join(", ", times.Select(t => F(t)))} ms; RangeSource per stream to temp files, then ffmpeg -c copy)" });
        }

        private async Task<int> ExportRange(int first, int last, string scratch, string outFile, CancellationToken ct) =>
            await SelfTest.ExportRangeAsync(_media, first, last, scratch, outFile, ct).ConfigureAwait(false);

        // ---- trickplay -------------------------------------------------------------------------------------------------

        private async Task TrickplaySection(CancellationToken ct)
        {
            var cap = options.Quick ? 300 : int.MaxValue;
            var chunks = _media.Chunks().Take(cap).ToList();
            var media = _media with { Runs = [.. ScanAnalysis.Runs(chunks)] };
            var data = DataFolder.Open(Path.Combine(_temp, "trickdata"), _root);
            var r = await Trickplay.GenerateAsync(data, "bench", media, new TrickplayOptions(DecodeMode.Gpu), null, ct).ConfigureAwait(false);
            var done = r.NewChunks;
            if (done == 0) { Add(CheckResult.Fail("bench.trickplay", "Trickplay, extrapolated to 2 h", "no thumbnails were produced")); return; }
            var c = BudgetRules.Trickplay(r.Elapsed.TotalSeconds, done);
            Add(c with { Value = c.Value + $", {r.Missing.Count} chunks skipped, decode {r.UsedMode}" });
        }

        // ---- CPU -------------------------------------------------------------------------------------------------------

        private async Task CpuSection(CancellationToken ct)
        {
            var cli = DiagnosticsEnvironment.FindMpvCli();
            var modes = _gpu ? new[] { DecodeMode.Gpu, DecodeMode.Cpu } : [DecodeMode.Cpu];
            foreach (var mode in modes)
            {
                var hwdec = mode == DecodeMode.Gpu ? (_renderHidden ? "d3d11va" : PlaybackRig.HeadlessGpuHwdec) : "no";
                var label = mode == DecodeMode.Gpu ? hwdec : "CPU decode";
                var start = Mid(0.3);
                var app = await MeasureApp(mode, start, ct).ConfigureAwait(false);
                var (appOne, appAll) = BudgetRules.CpuPercent(app.Cpu, app.Media, _cores);
                Add(CheckResult.Info($"bench.cpu.app.{(mode == DecodeMode.Gpu ? "gpu" : "cpu")}", $"CPU per media-second, app, {label}",
                    $"{F(appOne)} % of one core = {F(appAll, "0.00")} % of {_cores} logical cores ({F(app.Media, "0.0")} media s)", number: appOne, unit: "% of one core"));

                if (cli is null)
                {
                    Add(CheckResult.Skip($"bench.cpu.ref.{(mode == DecodeMode.Gpu ? "gpu" : "cpu")}", $"CPU per media-second, mpv CLI reference, {label}", "mpv.exe not found under .tools/mpv"));
                    continue;
                }
                var refRun = await MeasureCli(cli, start, hwdec, ct).ConfigureAwait(false);
                if (refRun is null)
                {
                    Add(CheckResult.Skip($"bench.cpu.ref.{(mode == DecodeMode.Gpu ? "gpu" : "cpu")}", $"CPU per media-second, mpv CLI reference, {label}", "the mpv CLI exited early"));
                    continue;
                }
                var (refOne, refAll) = BudgetRules.CpuPercent(refRun.Value.Cpu, refRun.Value.Wall, _cores);
                var ratio = BudgetRules.OverheadRatio(appOne, refOne);
                Add(CheckResult.Info($"bench.cpu.ref.{(mode == DecodeMode.Gpu ? "gpu" : "cpu")}", $"CPU per media-second, mpv CLI reference, {label}",
                    $"{F(refOne)} % of one core = {F(refAll, "0.00")} % of {_cores} logical cores; app is {F(ratio, "0.0")}x the reference", number: refOne, unit: "% of one core"));
            }
        }

        private async Task<(double Cpu, double Media)> MeasureApp(DecodeMode mode, double start, CancellationToken ct)
        {
            await using var rig = PlaybackRig.Create(mode, renderHidden: _renderHidden);
            await rig.PreWarmAsync(_media, ct).ConfigureAwait(false);
            await rig.Session.OpenAsync(_media, start, play: true, ct).ConfigureAwait(false);
            await Task.Delay(CpuWarm, ct).ConfigureAwait(false);
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var c0 = proc.TotalProcessorTime;
            var t0 = rig.TimePos;
            await Task.Delay(CpuWindow, ct).ConfigureAwait(false);
            proc.Refresh();
            var c1 = proc.TotalProcessorTime;
            var t1 = rig.TimePos;
            return ((c1 - c0).TotalSeconds, t1 - t0);
        }

        private async Task<(double Cpu, double Wall)?> MeasureCli(string exe, double start, string hwdec, CancellationToken ct)
        {
            var window = EdlWindow.Window(_media, start, EdlWindow.DefaultCount);
            var edlFile = Path.Combine(_temp, "reference.edl");
            await File.WriteAllTextAsync(edlFile, Edl.Build(_media, window, p => p), ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            // The same video output as the app run: a hidden window with mpv's D3D11 renderer, or vo=null.
            using var hiddenWindow = _renderHidden ? HiddenWindow.TryCreate() : null;
            var output = hiddenWindow is null
                ? new[] { "--vo=null" }
                : new[] { "--vo=gpu", "--gpu-api=d3d11", "--gpu-context=d3d11", "--wid=" + hiddenWindow.Wid };
            foreach (var a in output.Concat(
            [
                "--no-config", "--ao=null", "--no-terminal", "--really-quiet", "--hwdec=" + hwdec, "--pause=no", "--keep-open=no",
                "--hr-seek=yes", "--demuxer-max-bytes=" + MpvPlayerOptions.DefaultDemuxerMaxBytes.ToString(CultureInfo.InvariantCulture),
                "--demuxer-max-back-bytes=" + MpvPlayerOptions.DefaultDemuxerMaxBackBytes.ToString(CultureInfo.InvariantCulture),
                "--start=" + start.ToString("0.###", CultureInfo.InvariantCulture), edlFile,
            ])) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("the mpv CLI did not start");
            _ = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            try
            {
                await Task.Delay(CpuWarm, ct).ConfigureAwait(false);
                if (p.HasExited) return null;
                p.Refresh();
                var c0 = p.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                await Task.Delay(CpuWindow, ct).ConfigureAwait(false);
                if (p.HasExited) return null;
                p.Refresh();
                var c1 = p.TotalProcessorTime;
                return ((c1 - c0).TotalSeconds, sw.Elapsed.TotalSeconds);
            }
            finally
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }

        // ---- RAM -------------------------------------------------------------------------------------------------------

        private async Task RamSection(CancellationToken ct)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var proc = Process.GetCurrentProcess();
            double Rss() { proc.Refresh(); return proc.WorkingSet64 / 1048576.0; }
            var baseline = Rss();

            await using var rig = PlaybackRig.Create(_primary, renderHidden: _renderHidden);
            await rig.PreWarmAsync(_media, ct).ConfigureAwait(false);
            await rig.Session.OpenAsync(_media, Mid(0.1), play: true, ct).ConfigureAwait(false);

            var duration = SoakDuration;
            var reloadEvery = options.Quick ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(60);
            var sampleEvery = TimeSpan.FromSeconds(5);
            var warmup = options.Quick ? 20.0 : 90.0;           // seconds of playback before the slope is measured
            var xs = new List<double>();
            var ys = new List<double>();
            var peak = baseline;
            var sw = Stopwatch.StartNew();
            var nextReload = reloadEvery;
            var reloads = 0;
            var mediaStart = rig.TimePos;
            double lastPos = mediaStart, played = 0;
            while (sw.Elapsed < duration)
            {
                await Task.Delay(sampleEvery, ct).ConfigureAwait(false);
                var r = Rss();
                peak = Math.Max(peak, r);
                var pos = rig.TimePos;
                if (!double.IsNaN(pos) && pos > lastPos && pos - lastPos < 30) played += pos - lastPos;
                lastPos = double.IsNaN(pos) ? lastPos : pos;
                if (sw.Elapsed.TotalSeconds >= warmup) { xs.Add(sw.Elapsed.TotalMinutes); ys.Add(r); }
                if (sw.Elapsed >= nextReload)
                {
                    // the periodic reload (the session's own one fires every 600 media seconds, longer than this soak)
                    await rig.Session.ReloadAsync(pos >= _end - 12 ? _start : rig.Session.Position, ReloadCause.Policy, true, ct).ConfigureAwait(false);
                    lastPos = rig.TimePos;
                    nextReload += reloadEvery;
                    reloads++;
                }
                else if (!double.IsNaN(pos) && pos >= _end - 8)
                {
                    await rig.Session.SeekAsync(_start, ct).ConfigureAwait(false);       // a short session: start over
                    lastPos = rig.TimePos;
                }
            }
            var final = Rss();
            peak = Math.Max(peak, final);
            var slope = BudgetRules.Slope(xs, ys);

            var ram = BudgetRules.Ram("bench.ram", $"RAM after {F(sw.Elapsed.TotalMinutes, "0.0")} min of playback with periodic reload", peak);
            Add(ram with
            {
                Value = $"{F(peak)} MB peak ({F(peak - baseline)} MB over the {F(baseline)} MB before playback), {F(final)} MB at the end (whole process, working set); played {F(played, "0")} media s, {reloads} forced reloads",
                Note = peak < Budgets.RamMb ? null : "S1: the maintainer accepted RAM/CPU overhead unless it is a leak; the slope below is the leak signal",
            });
            Add(BudgetRules.Leak("bench.ram.slope", $"RAM slope after {F(warmup, "0")} s warm-up (leak signal)", slope));
            if (played < 0.8 * sw.Elapsed.TotalSeconds)
                Add(CheckResult.Fail("bench.ram.playing", "Playback kept running during the soak", $"played {F(played, "0")} media s in {F(sw.Elapsed.TotalSeconds, "0")} s"));
        }
    }
}
