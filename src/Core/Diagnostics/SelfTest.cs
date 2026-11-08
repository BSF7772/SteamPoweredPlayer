// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>
/// <c>SteamPoweredPlayer.exe --selftest &lt;out.txt&gt;</c>: a synthetic end-to-end check that needs neither Steam nor a real library
/// (plan §6). It lays out synthetic recordings for every case of plan §3 in a temp directory, then checks the scanner, headless libmpv
/// playback and seek accuracy, pause across reloads, speed, a Steam-style head trim under playback, EOF on a growing live session,
/// the lossless export and the trickplay sheets. Each check prints PASS or FAIL with the measured value; items of later phases print
/// <c>SKIP (Phase 3/6)</c> so the gap stays visible. Exit code 0 only when nothing failed. Never touches anything outside its own temp directory.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string reportPath, CancellationToken ct = default)
    {
        var report = new DiagnosticReport("selftest", "SteamPoweredPlayer --selftest (synthetic sessions, no Steam needed)");
        var runner = new Runner(report, reportPath);
        var sw = Stopwatch.StartNew();
        try { await runner.RunAsync(ct).ConfigureAwait(false); }
        catch (Exception e)
        {
            report.Add(CheckResult.Fail("selftest.crash", "Self-test crashed", Redactor.Describe(e)));
            report.Abort("unexpected exception");
        }
        runner.AddTimings();
        report.Add(CheckResult.Info("selftest.elapsed", "Elapsed", BudgetRules.F(sw.Elapsed.TotalSeconds) + " s", number: sw.Elapsed.TotalSeconds, unit: "s"));
        report.WriteTo(reportPath);
        return report.ExitCode;
    }

    /// <summary>Writes the lossless range of <paramref name="media"/> to <paramref name="outFile"/> (the export path of plan §4: RangeSource per stream, then ffmpeg -c copy). Returns the ffmpeg exit code.</summary>
    internal static async Task<int> ExportRangeAsync(SessionMedia media, int first, int last, string scratch, string outFile, CancellationToken ct)
    {
        var v = Path.Combine(scratch, "range-v.mp4");
        var a = Path.Combine(scratch, "range-a.mp4");
        foreach (var (stream, path) in new[] { (0, v), (1, a) })
        {
            await using var src = RangeSource.Create(media, stream, first, last);
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await src.CopyToAsync(dst, 1 << 20, ct).ConfigureAwait(false);
        }
        var r = await Ff.RunAsync(Ff.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", v, "-i", a, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-avoid_negative_ts", "make_zero", outFile], ct).ConfigureAwait(false);
        return r.ExitCode;
    }

    private sealed class Runner(DiagnosticReport report, string reportPath)
    {
        private const double FrameTolerance = SyntheticSession.FrameSeconds + 0.005;
        private SyntheticSession _s = null!;
        private bool _gpu;

        private void Add(CheckResult c)
        {
            report.Add(c);
            report.WriteTo(reportPath);
        }

        // Development aid: SPP_SELFTEST_ONLY=play.live,scan runs only the checks whose id starts with one of the prefixes.
        private static readonly string[] OnlyPrefixes =
            (Environment.GetEnvironmentVariable("SPP_SELFTEST_ONLY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static bool Wanted(string id) => OnlyPrefixes.Length == 0 || OnlyPrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal));

        private readonly List<(string Id, double Seconds)> _timings = [];

        private async Task Step(string id, string name, Func<Task<CheckResult>> body)
        {
            if (!Wanted(id)) return;
            var sw = Stopwatch.StartNew();
            try { Add(await body().ConfigureAwait(false)); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Add(CheckResult.Fail(id, name, "exception", Redactor.Describe(e)));
            }
            _timings.Add((id, sw.Elapsed.TotalSeconds));
        }

        /// <summary>D-TEST keeps <c>--selftest</c> under 30 s: the slowest steps are printed so a regression is easy to find.</summary>
        public void AddTimings() =>
            report.Add(CheckResult.Info("selftest.timings", "Step timings, slowest first",
                string.Join(", ", _timings.OrderByDescending(t => t.Seconds).Take(40).Select(t => $"{t.Id} {F(t.Seconds, "0.0")} s"))
                + $"; all steps {F(_timings.Where(t => t.Id != "env.hardware").Sum(t => t.Seconds), "0.0")} s"));

        private static string F(double v, string f = "0.#") => BudgetRules.F(v, f);

        public async Task RunAsync(CancellationToken ct)
        {
            var setup = Stopwatch.StartNew();
            report.AddHeader("Hardware: " + HardwareClass.Detect().Describe());
            _timings.Add(("env.hardware", setup.Elapsed.TotalSeconds));
            Ff.Locate();
            if (!Ff.Available)
            {
                Add(CheckResult.Fail("env.ffmpeg", "ffmpeg and ffprobe found", "not found"));
                report.Abort("ffmpeg is required to generate the synthetic sessions");
                return;
            }
            var ver = (await Ff.RunAsync(Ff.Ffmpeg, ["-hide_banner", "-version"], ct).ConfigureAwait(false)).StdOut.Split('\n')[0].Trim();
            Add(CheckResult.Pass("env.ffmpeg", "ffmpeg and ffprobe found", Redactor.Scrub(ver)));

            DiagnosticsEnvironment.PrepareMpv();
            if (!MpvLibrary.IsAvailable())
            {
                Add(CheckResult.Fail("env.libmpv", "libmpv loads", "libmpv-2.dll not found"));
                report.Abort("libmpv is required for the playback checks");
                return;
            }
            await using (var probe = PlaybackRig.Create(DecodeMode.Cpu))
                Add(CheckResult.Pass("env.libmpv", "libmpv loads", "mpv " + (probe.Player.GetString("mpv-version") ?? "?")));
            _timings.Add(("env", setup.Elapsed.TotalSeconds));

            var gen = Stopwatch.StartNew();
            _s = await SyntheticSession.CreateAsync(parent: null, ct: ct).ConfigureAwait(false);
            using (_s)
            {
                Add(CheckResult.Pass("synth.generate", "Synthetic session generated", $"{_s.Chunks} chunks x 3 s (320x180, 30 fps) in {F(gen.Elapsed.TotalSeconds)} s"));
                _timings.Add(("synth.generate", gen.Elapsed.TotalSeconds));

                await ScannerChecks(ct).ConfigureAwait(false);
                var probeGpu = Stopwatch.StartNew();
                await DetectGpu(ct).ConfigureAwait(false);
                _timings.Add(("env.hwdec", probeGpu.Elapsed.TotalSeconds));
                await PlaybackChecks(DecodeMode.Cpu, ct).ConfigureAwait(false);
                if (_gpu) await PlaybackChecks(DecodeMode.Gpu, ct).ConfigureAwait(false);
                await Step("play.pause-reload", "Pause preserved across reloads", () => PauseAcrossReloads(ct)).ConfigureAwait(false);
                await Step("play.speed2x", "Speed 2x and chunk boundaries", () => SpeedTwo(ct)).ConfigureAwait(false);
                await Step("play.head-trim", "Steam-style head trim under playback", () => HeadTrim(ct)).ConfigureAwait(false);
                await Step("play.trim-catch-up", "Trim past the paused playhead", () => TrimCatchUp(ct)).ConfigureAwait(false);
                await Step("play.live-eof", "EOF on a live session continues after growth", () => LiveEof(ct)).ConfigureAwait(false);
                await Step("export.lossless", "Lossless 30 s export (RangeSource + ffmpeg -c copy)", () => Export(ct)).ConfigureAwait(false);
                await Step("trickplay.sheets", "Trickplay sheets generated", () => TrickplayChecks(ct)).ConfigureAwait(false);
                await Step("trickplay.live", "Trickplay follows a growing session", () => TrickplayLiveChecks(ct)).ConfigureAwait(false);
                await Step("play.live-trickplay", "Playback is undisturbed while trickplay builds", () => PlayWhileTrickplayBuilds(ct)).ConfigureAwait(false);
                await Step("export.presets", "Every export preset encodes", async () =>
                    await ExportSelfChecks.PresetsAsync(await NormalMedia(ct).ConfigureAwait(false), _s.Scratch("presets"), ct).ConfigureAwait(false)).ConfigureAwait(false);
                await Step("export.sync", "Audio-sync detect and fix", () => ExportSelfChecks.SyncAsync(_s.Scratch("sync"), ct)).ConfigureAwait(false);
                await Step("net.download", "Interrupted download", async () =>
                    await ExportSelfChecks.InterruptedDownloadAsync(await NormalMedia(ct).ConfigureAwait(false), _s.Scratch("download"), ct).ConfigureAwait(false)).ConfigureAwait(false);
                LaterPhases();
            }
        }

        // ---- scanner ----------------------------------------------------------------------------------------------

        private static string Runs(SessionMedia? m) => m is null ? "-" : string.Join(',', m.Runs.Select(r => r.First == r.Last ? $"{r.First}" : $"{r.First}-{r.Last}"));

        private static string Describe(ScanResult r) =>
            $"{r.Status}, runs {Runs(r.Media)}, {r.Media?.ChunkCount ?? 0} chunks, manifest {r.Manifest}, live {r.Media?.Live ?? false}, crashed {r.Crashed}"
            + (r.Issues.Count == 0 ? "" : ", issues " + string.Join('/', r.Issues.Select(i => i.Kind + (i.Chunk is { } c ? "#" + c.ToString(CultureInfo.InvariantCulture) : ""))));

        private async Task ScannerChecks(CancellationToken ct)
        {
            // The cases are independent folders: they are built and scanned in parallel (D-TEST), and reported in order.
            var pending = new List<(string Id, string Name, Task<ScanResult> Scan, Func<ScanResult, bool> Ok)>();
            Task Case(string id, string name, SyntheticVariant v, Func<ScanResult, bool> ok)
            {
                if (Wanted(id)) pending.Add((id, name, Task.Run(() => Scanner.ScanAsync(_s.Build(v), null, ct), ct), ok));
                return Task.CompletedTask;
            }

            await Case("scan.normal", "Scanner: normal session", SyntheticVariant.Normal, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "1-40" && !r.Media!.Live && !r.Crashed && r.Manifest == ManifestKind.Static && r.Issues.Count == 0).ConfigureAwait(false);
            await Case("scan.trimmed-head", "Scanner: trimmed head, stale manifest", SyntheticVariant.TrimmedHead, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "11-40" && r.Media!.FirstChunk == 11 && !r.Crashed).ConfigureAwait(false);
            await Case("scan.stale-manifest", "Scanner: stale manifest is ignored", SyntheticVariant.StaleManifest, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "1-40" && r.Manifest == ManifestKind.Static && !r.Crashed).ConfigureAwait(false);
            await Case("scan.zero-manifest", "Scanner: zero manifest", SyntheticVariant.ZeroManifest, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "1-40" && r.Manifest == ManifestKind.ZeroFilled && r.Crashed
                && r.Issues.Any(i => i.Kind == ScanIssueKind.ZeroFilledManifest)).ConfigureAwait(false);
            await Case("scan.gap", "Scanner: gap gives two runs", SyntheticVariant.Gap, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "1-15,21-40" && r.Media!.ChunkCount == 35).ConfigureAwait(false);
            await Case("scan.live", "Scanner: live session", SyntheticVariant.Live, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "1-12" && r.Media!.Live && r.Manifest == ManifestKind.Dynamic && !r.Crashed).ConfigureAwait(false);
            await Case("scan.crashed", "Scanner: crashed (zero manifest + stale .tmp)", SyntheticVariant.Crashed, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "1-40" && r.Crashed && !r.Media!.Live
                && r.Issues.Any(i => i.Kind == ScanIssueKind.StaleTemporaryChunk) && r.Issues.Any(i => i.Kind == ScanIssueKind.ZeroFilledManifest)).ConfigureAwait(false);
            await Case("scan.missing-audio", "Scanner: missing audio chunk", SyntheticVariant.MissingAudioChunk, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "1-7,9-20" && r.Issues.Any(i => i.Kind == ScanIssueKind.MissingAudioChunk && i.Chunk == 8)).ConfigureAwait(false);
            await Case("scan.zero-chunk", "Scanner: zero-filled chunk with a normal name", SyntheticVariant.ZeroFilledChunk, r =>
                r.Status == ScanStatus.Playable && Runs(r.Media) == "1-8,10-20" && r.Issues.Any(i => i.Kind == ScanIssueKind.ZeroFilledChunk && i.Chunk == 9)).ConfigureAwait(false);
            foreach (var (id, name, scan, ok) in pending)
                await Step(id, name, async () =>
                {
                    var r = await scan.ConfigureAwait(false);
                    return CheckResult.From(ok(r), id, name, Describe(r));
                }).ConfigureAwait(false);
        }

        // ---- playback ---------------------------------------------------------------------------------------------

        private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, int pollMs = 20)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (condition()) return true;
                await Task.Delay(pollMs).ConfigureAwait(false);
            }
            return condition();
        }

        private SessionMedia? _normal;

        /// <summary>One shared normal session for the checks that only read it (D-TEST: building a copy per check cost ~0.3 s each).</summary>
        private async Task<SessionMedia> NormalMedia(CancellationToken ct) => _normal ??= await MediaOf(_s.Build(SyntheticVariant.Normal), ct).ConfigureAwait(false);

        private async Task<SessionMedia> MediaOf(string folder, CancellationToken ct) =>
            (await Scanner.ScanAsync(folder, null, ct).ConfigureAwait(false)).Media ?? throw new InvalidOperationException("synthetic session is not playable");

        private async Task DetectGpu(CancellationToken ct)
        {
            try
            {
                var media = await NormalMedia(ct).ConfigureAwait(false);
                await using var rig = PlaybackRig.Create(DecodeMode.Gpu);
                await rig.Session.OpenAsync(media, 0, play: true, ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                _gpu = rig.UsesHardware;
                Add(_gpu
                    ? CheckResult.Info("env.hwdec", "Hardware decode in headless mpv", "hwdec-current " + rig.HwdecCurrent)
                    : CheckResult.Skip("env.hwdec", "Hardware decode in headless mpv", "not available: mpv decodes in software"));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Add(CheckResult.Skip("env.hwdec", "Hardware decode in headless mpv", "probe failed: " + Redactor.Describe(e)));
            }
        }

        private static string ModeName(DecodeMode m) => m == DecodeMode.Gpu ? "hwdec d3d11va" : "hwdec=no";
        private static string ModeId(DecodeMode m) => m == DecodeMode.Gpu ? "gpu" : "cpu";

        private async Task PlaybackChecks(DecodeMode mode, CancellationToken ct)
        {
            await Step($"play.lead-edl.{ModeId(mode)}", $"Lead EDL plays headless ({ModeName(mode)})", async () =>
            {
                var media = await NormalMedia(ct).ConfigureAwait(false);
                await using var rig = PlaybackRig.Create(mode);
                var sw = Stopwatch.StartNew();
                await rig.Session.OpenAsync(media, null, play: true, ct).ConfigureAwait(false);
                var open = sw.Elapsed.TotalMilliseconds;
                await Task.Delay(300, ct).ConfigureAwait(false);      // past the decoder's start-up, then 1 s of steady playback
                var t0 = rig.TimePos;
                sw.Restart();
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var advanced = rig.TimePos - t0;
                var wall = sw.Elapsed.TotalSeconds;
                var ok = advanced > 0.8 * wall && advanced < 1.25 * wall && (mode == DecodeMode.Cpu || rig.UsesHardware) && (mode == DecodeMode.Gpu || !rig.UsesHardware);
                return CheckResult.From(ok, $"play.lead-edl.{ModeId(mode)}", $"Lead EDL plays headless ({ModeName(mode)})",
                    $"advanced {F(advanced, "0.00")} s in {F(wall, "0.00")} s, first frame {F(open)} ms, hwdec-current {rig.HwdecCurrent}");
            }).ConfigureAwait(false);

            await Step($"play.seek.{ModeId(mode)}", $"Seek-by-reload accuracy +-1 frame ({ModeName(mode)})", async () =>
            {
                var media = await NormalMedia(ct).ConfigureAwait(false);
                var rng = new Random(20261107);
                var targets = new List<double>();
                for (var i = 0; i < 20; i++) targets.Add(Math.Round(rng.NextDouble() * 119, 3));
                // backward revisits (the case a plain mpv seek breaks), chunk boundaries, the very end
                targets.AddRange([60.0, 55.3, 12.7, 12.4, 100.9, 99.5, 3.0, 2.99, 9.01, 117.0, 0.2, 59.99, 60.01, 87.0, 86.9, 119.8, 119.9, 119.99]);
                return await SeekAccuracy($"play.seek.{ModeId(mode)}", $"Seek-by-reload accuracy +-1 frame ({ModeName(mode)})", media, mode, targets, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await Step($"play.seek-gap.{ModeId(mode)}", $"Seek across the gap ({ModeName(mode)})", async () =>
            {
                var media = await MediaOf(_s.Build(SyntheticVariant.Gap), ct).ConfigureAwait(false);
                // runs 1-15 (0..45 s) and 21-40 (60..120 s); targets inside the gap snap to the next run's start
                double[] targets = [10.0, 70.0, 43.0, 44.0, 44.5, 44.7, 44.75, 44.8, 44.85, 44.9, 44.95, 44.99, 50.0, 59.99, 61.0, 20.0, 119.0, 45.0, 30.0, 90.0, 40.0, 62.5, 5.0, 100.0, 46.0, 59.0, 30.2];
                return await SeekAccuracy($"play.seek-gap.{ModeId(mode)}", $"Seek across the gap ({ModeName(mode)})", media, mode, targets, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        private static async Task<CheckResult> SeekAccuracy(string id, string name, SessionMedia media, DecodeMode mode, IReadOnlyList<double> targets, CancellationToken ct)
        {
            // D-TEST (--selftest < 30 s): the copy-back hardware path reloads 10-20x slower than software decode on tiny frames, and a
            // busy GPU (a game running) makes it up to a second per reload. The hardware pass takes every 4th target and stops after
            // 2.5 s once 4 seeks are done; software decode runs every target.
            if (mode == DecodeMode.Gpu) targets = [.. targets.Where((_, i) => i % 4 == 0)];
            await using var rig = PlaybackRig.Create(mode);
            await rig.Session.OpenAsync(media, null, play: false, ct).ConfigureAwait(false);
            double worst = 0, worstTarget = 0, worstAt = 0;
            var bad = 0;
            var times = new List<double>();
            var sw = new Stopwatch();
            var budget = Stopwatch.StartNew();
            var done = 0;
            foreach (var t in targets)
            {
                if (mode == DecodeMode.Gpu && done >= 4 && budget.Elapsed > TimeSpan.FromSeconds(2.5)) break;
                done++;
                var expected = PlaybackSession.Snap(media, t);
                sw.Restart();
                await rig.Session.SeekAsync(t, ct).ConfigureAwait(false);
                times.Add(sw.Elapsed.TotalMilliseconds);
                var err = Math.Abs(rig.TimePos - expected);
                if (double.IsNaN(err)) err = double.PositiveInfinity;
                if (err > worst) { worst = err; worstTarget = t; worstAt = rig.TimePos; }
                if (err > FrameTolerance || rig.Player.GetFlag("pause") != true) bad++;
            }
            return CheckResult.From(bad == 0, id, name,
                $"{done} seeks, max error {F(worst * 1000)} ms (limit {F(FrameTolerance * 1000)}), {bad} off{(bad > 0 ? $" (worst: target {F(worstTarget)} s, snapped {F(PlaybackSession.Snap(media, worstTarget))} s, landed {F(worstAt, "0.000")} s)" : "")}, median reload {F(BudgetRules.Median(times))} ms, hwdec-current {rig.HwdecCurrent}",
                number: worst * 1000, unit: "ms", budget: "<= " + F(FrameTolerance * 1000) + " ms");
        }

        private async Task<CheckResult> PauseAcrossReloads(CancellationToken ct)
        {
            var media = await NormalMedia(ct).ConfigureAwait(false);
            await using var rig = PlaybackRig.Create(DecodeMode.Cpu);
            var s = rig.Session;
            await s.OpenAsync(media, 3, play: true, ct).ConfigureAwait(false);
            var playing = rig.Player.GetFlag("pause") == false;
            s.SetPause(true);
            await s.SeekAsync(12, ct).ConfigureAwait(false);
            var pausedAfterSeek = rig.Player.GetFlag("pause") == true;
            var p0 = rig.TimePos;
            await Task.Delay(400, ct).ConfigureAwait(false);
            var still = Math.Abs(rig.TimePos - p0) < 0.02;
            s.SetPause(false);
            await s.SeekAsync(6, ct).ConfigureAwait(false);
            var playingAfterSeek = rig.Player.GetFlag("pause") == false;
            var p1 = rig.TimePos;
            await Task.Delay(600, ct).ConfigureAwait(false);
            var moved = rig.TimePos - p1;
            // a frame step is a pause, kept across the next reload
            s.FrameStep();
            await s.SeekAsync(20, ct).ConfigureAwait(false);
            var stepPaused = rig.Player.GetFlag("pause") == true && s.UserPaused;
            var ok = playing && pausedAfterSeek && still && playingAfterSeek && moved > 0.3 && stepPaused;
            return CheckResult.From(ok, "play.pause-reload", "Pause preserved across reloads",
                $"paused->reload stays paused {pausedAfterSeek} (still {still}), playing->reload keeps playing {playingAfterSeek} (moved {F(moved, "0.00")} s in 0.6 s), frame-step pause kept {stepPaused}");
        }

        private async Task<CheckResult> SpeedTwo(CancellationToken ct)
        {
            var media = await NormalMedia(ct).ConfigureAwait(false);
            await using var rig = PlaybackRig.Create(DecodeMode.Cpu);
            await rig.Session.OpenAsync(media, 2, play: true, ct).ConfigureAwait(false);
            rig.Session.SetSpeed(2);
            await Task.Delay(300, ct).ConfigureAwait(false);
            var t0 = rig.TimePos;
            var sw = Stopwatch.StartNew();
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var rate = (rig.TimePos - t0) / sw.Elapsed.TotalSeconds;
            var speedOk = Math.Abs(rig.Player.GetDouble("speed") - 2) < 1e-6;

            // 4x across two chunk boundaries (9 s and 12 s)
            await rig.Session.SeekAsync(8.4, ct).ConfigureAwait(false);
            rig.Session.SetPause(false);
            rig.Session.SetSpeed(4);
            sw.Restart();
            var crossed = await WaitFor(() => rig.TimePos >= 12.3, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            return CheckResult.From(rate is >= 1.6 and <= 2.4 && speedOk && crossed, "play.speed2x", "Speed 2x and chunk boundaries",
                $"2x plays at {F(rate, "0.00")}x, 4x crossed two chunk boundaries in {F(sw.Elapsed.TotalMilliseconds)} ms: {crossed}", number: rate, unit: "x");
        }

        /// <summary>Monitors time-pos while playing: the longest time it stood still and how much it advanced.</summary>
        private sealed class StallMonitor(Func<double> position)
        {
            private readonly Stopwatch _wall = Stopwatch.StartNew();
            public double MaxStallMs;
            public double Advanced;
            public double Elapsed => _wall.Elapsed.TotalSeconds;

            public async Task RunAsync(TimeSpan duration, CancellationToken ct)
            {
                var last = position();
                var first = last;
                var lastMoveAt = _wall.Elapsed;
                while (_wall.Elapsed < duration)
                {
                    await Task.Delay(40, ct).ConfigureAwait(false);
                    var p = position();
                    if (double.IsNaN(p)) continue;
                    if (p - last >= 0.01 || p < last - 0.01) { last = p; lastMoveAt = _wall.Elapsed; }
                    MaxStallMs = Math.Max(MaxStallMs, (_wall.Elapsed - lastMoveAt).TotalMilliseconds);
                }
                Advanced = last - first;
            }
        }

        private async Task<CheckResult> HeadTrim(CancellationToken ct)
        {
            var folder = _s.Build(SyntheticVariant.Normal);
            var media = await MediaOf(folder, ct).ConfigureAwait(false);
            await using var rig = PlaybackRig.Create(DecodeMode.Cpu);
            using var follower = new CacheFollower(rig.Session);
            var failures = 0;
            var reloads = 0;
            var trimmedEvents = 0;
            rig.Session.ReloadFailed += _ => Interlocked.Increment(ref failures);
            rig.Session.Reloaded += _ => Interlocked.Increment(ref reloads);
            follower.Trimmed += _ => Interlocked.Increment(ref trimmedEvents);

            // D-TEST: a chunk every 0.6 s (Steam: one per 3 s) keeps the check short; the playhead starts right after the trimmed chunks.
            const int lastTrimmed = 3;
            var trimEvery = TimeSpan.FromSeconds(0.6);
            await rig.Session.OpenAsync(media, lastTrimmed * media.SegmentSeconds, play: true, ct).ConfigureAwait(false);
            var gate = new SemaphoreSlim(1);
            using var trimmer = _s.StartHeadTrim(folder, 1, lastTrimmed, trimEvery);
            trimmer.Done += _ => Task.Run(async () =>
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var m = (await Scanner.ScanAsync(folder, null, CancellationToken.None).ConfigureAwait(false)).Media;
                    if (m is not null) await follower.OnMediaChangedAsync(m, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception) { Interlocked.Increment(ref failures); }
                finally { gate.Release(); }
            }, CancellationToken.None);

            await Task.Delay(300, ct).ConfigureAwait(false);
            var mon = new StallMonitor(() => rig.TimePos);
            await mon.RunAsync(trimEvery * lastTrimmed + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            await gate.WaitAsync(ct).ConfigureAwait(false);      // the last rescan finished
            gate.Release();

            var left = (await Scanner.ScanAsync(folder, null, ct).ConfigureAwait(false)).Media;
            var ok = failures == 0 && mon.MaxStallMs < 1000 && mon.Advanced >= 0.85 * mon.Elapsed && left?.FirstChunk == lastTrimmed + 1 && !rig.Session.AtEnd;
            return CheckResult.From(ok, "play.head-trim", "Steam-style head trim under playback",
                $"{lastTrimmed} head chunks deleted (one per {F(trimEvery.TotalSeconds)} s), played {F(mon.Advanced, "0.0")} s in {F(mon.Elapsed, "0.0")} s, max stall {F(mon.MaxStallMs)} ms, reloads {reloads}, failures {failures}, jumps {trimmedEvents}",
                number: mon.MaxStallMs, unit: "ms", budget: "stall < 1000 ms");
        }

        private async Task<CheckResult> TrimCatchUp(CancellationToken ct)
        {
            var folder = _s.Build(SyntheticVariant.Normal);
            var media = await MediaOf(folder, ct).ConfigureAwait(false);
            await using var rig = PlaybackRig.Create(DecodeMode.Cpu);
            using var follower = new CacheFollower(rig.Session);
            CacheTrim? trim = null;
            follower.Trimmed += t => trim = t;
            await rig.Session.OpenAsync(media, 21.0, play: false, ct).ConfigureAwait(false);       // paused inside chunk 8

            for (var n = 1; n <= 10; n++) SyntheticSession.DeleteChunk(folder, n);                 // Steam catches up with the playhead
            var updated = await MediaOf(folder, ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            await follower.OnMediaChangedAsync(updated, ct).ConfigureAwait(false);
            var reload = sw.Elapsed.TotalMilliseconds;

            var head = updated.ChunkStartSeconds(updated.FirstChunk!.Value);
            var landed = Math.Abs(rig.TimePos - head) <= FrameTolerance;
            var stayedPaused = rig.Player.GetFlag("pause") == true && rig.Session.UserPaused;
            rig.Session.SetPause(false);
            var p0 = rig.TimePos;
            await Task.Delay(800, ct).ConfigureAwait(false);
            var moved = rig.TimePos - p0;
            var ok = trim is { Emptied: false } && Math.Abs(trim.ToSeconds - head) < 1e-6 && landed && stayedPaused && moved > 0.4;
            return CheckResult.From(ok, "play.trim-catch-up", "Trim past the paused playhead",
                $"jumped from {F(21.0)} s to the new head {F(head)} s (Steam removed {F(trim?.RemovedSeconds ?? double.NaN)} s ahead of the playhead) in {F(reload)} ms, still paused {stayedPaused}, then played {F(moved, "0.00")} s in 0.8 s");
        }

        private async Task<CheckResult> LiveEof(CancellationToken ct)
        {
            var folder = _s.Build(SyntheticVariant.Live);
            var media = await MediaOf(folder, ct).ConfigureAwait(false);
            await using var rig = PlaybackRig.Create(DecodeMode.Cpu);
            using var follower = new CacheFollower(rig.Session);
            var gate = new SemaphoreSlim(1);
            var failures = new List<string>();
            var reloadLog = new List<string>();
            rig.Session.Reloaded += r => { lock (reloadLog) reloadLog.Add($"{r.Cause}@{r.StartSeconds:0.0}"); };
            rig.Session.ReloadFailed += e => { lock (failures) failures.Add(e.GetType().Name + ": " + Redactor.Scrub(e.Message)); };
            var live = media.Live;
            var chunks = media.ChunkCount;
            using var writer = _s.StartLive(folder, 13, TimeSpan.FromSeconds(1.2));
            writer.Done += _ => Task.Run(async () =>
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var m = (await Scanner.ScanAsync(folder, null, CancellationToken.None).ConfigureAwait(false)).Media;
                    if (m is null) return;
                    live = m.Live;
                    chunks = m.ChunkCount;
                    await follower.OnMediaChangedAsync(m, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) { lock (failures) failures.Add("follower " + e.GetType().Name + ": " + Redactor.Scrub(e.Message)); }
                finally { gate.Release(); }
            }, CancellationToken.None);

            var oldEnd = media.ChunkEndSeconds(media.LastChunk!.Value);                 // 36 s (the tfdt axis)
            var eof = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Session.ReachedEnd += pos => eof.TrySetResult(pos);
            await rig.Session.OpenAsync(media, oldEnd - 0.5, play: true, ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var reachedEnd = await Task.WhenAny(eof.Task, Task.Delay(8000, ct)).ConfigureAwait(false) == eof.Task;
            var eofAt = sw.Elapsed.TotalMilliseconds;
            var resumed = await WaitFor(() => rig.TimePos >= oldEnd + 2.0, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            var resumedAt = sw.Elapsed.TotalMilliseconds;
            var continued = await WaitFor(() => rig.TimePos >= oldEnd + 3.2, TimeSpan.FromSeconds(12)).ConfigureAwait(false);   // into the second new chunk
            var ok = reachedEnd && resumed && continued && live && !rig.Session.AtEnd;
            return CheckResult.From(ok, "play.live-eof", "EOF on a live session continues after growth",
                $"EOF after {F(eofAt)} ms, passed the old end by 2 s after {F(resumedAt)} ms, then by 3.2 s: {continued}; session {chunks} chunks and still live: {live}; reloads [{string.Join(' ', reloadLog)}], final time-pos {F(rig.TimePos, "0.00")} s, at end {rig.Session.AtEnd}, mpv pause {rig.Player.GetFlag("pause")} eof {rig.Player.GetFlag("eof-reached")} core-idle {rig.Player.GetFlag("core-idle")} cache {rig.Player.GetFlag("paused-for-cache")}, window {string.Join('-', rig.Session.Window.Take(1).Concat(rig.Session.Window.TakeLast(1)))}" + (failures.Count == 0 ? "" : "; errors: " + string.Join(" | ", failures.Take(3))));
        }

        // ---- export, trickplay --------------------------------------------------------------------------------------

        private static async Task<(double? Start, double? Duration)> ProbeStream(string file, string kind, CancellationToken ct)
        {
            var r = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-select_streams", kind, "-show_entries", "stream=start_time,duration", "-of", "csv=p=0", file], ct).ConfigureAwait(false);
            var parts = r.StdOut.Trim().Split(',');
            double? Num(int i) => i < parts.Length && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            return (Num(0), Num(1));
        }

        private async Task<CheckResult> Export(CancellationToken ct)
        {
            var media = await NormalMedia(ct).ConfigureAwait(false);
            var scratch = _s.Scratch("export");
            var outFile = Path.Combine(scratch, "out.mp4");
            var sw = Stopwatch.StartNew();
            var code = await ExportRangeAsync(media, 5, 14, scratch, outFile, ct).ConfigureAwait(false);
            var took = sw.Elapsed.TotalMilliseconds;
            if (code != 0) return CheckResult.Fail("export.lossless", "Lossless 30 s export (RangeSource + ffmpeg -c copy)", "ffmpeg exit code " + code.ToString(CultureInfo.InvariantCulture));
            var (vs, vd) = await ProbeStream(outFile, "v:0", ct).ConfigureAwait(false);
            var (aStart, ad) = await ProbeStream(outFile, "a:0", ct).ConfigureAwait(false);
            var sync = vs is { } x && aStart is { } y ? Math.Abs(x - y) : double.NaN;
            var ok = vd is { } d1 && Math.Abs(d1 - 30) <= 0.1 && ad is { } d2 && Math.Abs(d2 - 30) <= 0.1 && sync <= 0.1;
            return CheckResult.From(ok, "export.lossless", "Lossless 30 s export (RangeSource + ffmpeg -c copy)",
                $"video {F(vd ?? double.NaN, "0.000")} s, audio {F(ad ?? double.NaN, "0.000")} s, A/V start offset {F(sync * 1000)} ms, took {F(took)} ms",
                number: took, unit: "ms");
        }

        private async Task<CheckResult> TrickplayChecks(CancellationToken ct)
        {
            var folder = _s.Build(SyntheticVariant.Normal);
            var media = await MediaOf(folder, ct).ConfigureAwait(false);
            var data = DataFolder.Open(_s.Scratch("data"), _s.RecordingRoot);
            var r = await Trickplay.GenerateAsync(data, "selftest", media, new TrickplayOptions(DecodeMode.Cpu), null, ct).ConfigureAwait(false);
            var dir = Trickplay.DirFor(data, "selftest");
            var manifest = TrickplayManifest.TryRead(dir);
            var sheet = manifest?.Sheets.FirstOrDefault();
            var jpegOk = false;
            int w = 0, h = 0;
            if (sheet is not null) jpegOk = Trickplay.TryGetJpegSize(await File.ReadAllBytesAsync(Path.Combine(dir, sheet.File), ct).ConfigureAwait(false), out w, out h);
            var index = TrickplayIndex.TryLoad(data, "selftest");
            var ok = r.NewChunks == media.ChunkCount && r.Missing.Count == 0 && manifest?.Sheets.Count == 1 && jpegOk && index?.Count == media.ChunkCount;
            return CheckResult.From(ok, "trickplay.sheets", "Trickplay sheets generated",
                $"{r.NewChunks}/{media.ChunkCount} tiles in {r.SheetsWritten} sheet ({w}x{h}), {r.Missing.Count} missing, {F(r.Elapsed.TotalMilliseconds)} ms, decode {r.UsedMode}");
        }

        /// <summary>A session that is still being written: every chunk but the open newest one gets a tile within one chunk interval of its arrival.</summary>
        private async Task<CheckResult> TrickplayLiveChecks(CancellationToken ct)
        {
            const double budgetMs = 3000;      // one chunk interval
            var folder = _s.Build(SyntheticVariant.Live);
            var data = DataFolder.Open(_s.Scratch("data-live"), _s.RecordingRoot);
            var first = await MediaOf(folder, ct).ConfigureAwait(false);
            var start = TrickplayLive.Buildable(first);
            var r0 = start is null ? null : await Trickplay.GenerateAsync(data, "live", start, new TrickplayOptions(DecodeMode.Cpu), null, ct).ConfigureAwait(false);

            var worst = 0.0;
            var cpu = 0.0;
            var tiles = 0;
            var grown = 0;
            for (var n = first.ChunkCount + 1; n <= first.ChunkCount + 4; n++)
            {
                _s.AddChunk(folder, n);
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var media = await MediaOf(folder, ct).ConfigureAwait(false);
                var buildable = TrickplayLive.Buildable(media);
                if (buildable is null) break;
                var r = await Trickplay.GenerateAsync(data, "live", buildable, new TrickplayOptions(DecodeMode.Cpu), null, ct).ConfigureAwait(false);
                var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                worst = Math.Max(worst, ms);
                cpu += r.Elapsed.TotalMilliseconds;
                tiles += r.NewChunks;
                if (r.NewChunks == 1) grown++;
            }
            var index = TrickplayIndex.TryLoad(data, "live");
            var ok = r0 is { NewChunks: > 0 } && grown == 4 && worst < budgetMs && index?.Count == first.ChunkCount + 3;
            return CheckResult.From(ok, "trickplay.live", "Trickplay follows a growing session",
                $"{r0?.NewChunks ?? 0} tiles at start, then {grown}/4 new chunks each got one tile (worst latency {F(worst)} ms of {F(budgetMs)}, {F(tiles > 0 ? cpu / tiles : 0)} ms CPU per tile), index {index?.Count ?? 0} tiles",
                number: worst, unit: "ms");
        }

        /// <summary>Playing a growing session while trickplay builds a tile per new chunk must not drop frames or stall (the builder runs beside the player as ffmpeg child processes).</summary>
        private async Task<CheckResult> PlayWhileTrickplayBuilds(CancellationToken ct)
        {
            var folder = _s.Build(SyntheticVariant.Live);
            var media = await MediaOf(folder, ct).ConfigureAwait(false);
            var data = DataFolder.Open(_s.Scratch("data-live-play"), _s.RecordingRoot);
            await using var rig = PlaybackRig.Create(DecodeMode.Cpu);
            await rig.Session.OpenAsync(media, 0, play: true, ct).ConfigureAwait(false);
            long Drops() => (rig.Player.GetInt64("frame-drop-count") ?? 0) + (rig.Player.GetInt64("decoder-frame-drop-count") ?? 0);

            await WaitFor(() => rig.TimePos >= 0.5, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var quietFrom = Drops();
            var quietT0 = rig.TimePos;
            await Task.Delay(600, ct).ConfigureAwait(false);
            var quietDrops = Drops() - quietFrom;
            var quietSeconds = rig.TimePos - quietT0;

            var busyFrom = Drops();
            var busyT0 = rig.TimePos;
            var builds = 0;
            var tiles = 0;
            var maxGap = 0.0;
            var lastPos = rig.TimePos;
            var lastAt = Stopwatch.GetTimestamp();
            using var writer = _s.StartLive(folder, 13, TimeSpan.FromSeconds(0.9));
            var gate = new SemaphoreSlim(1);
            writer.Done += _ => Task.Run(async () =>
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var m = (await Scanner.ScanAsync(folder, null, CancellationToken.None).ConfigureAwait(false)).Media;
                    var b = m is null ? null : TrickplayLive.Buildable(m);
                    if (b is null) return;
                    var r = await Trickplay.GenerateAsync(data, "live-play", b, new TrickplayOptions(DecodeMode.Cpu), null, CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Increment(ref builds);
                    Interlocked.Add(ref tiles, r.NewChunks);
                }
                catch (Exception e) when (e is IOException or InvalidOperationException) { }
                finally { gate.Release(); }
            }, CancellationToken.None);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(2.8))
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
                var pos = rig.TimePos;
                if (pos > lastPos + 0.001) { lastPos = pos; lastAt = Stopwatch.GetTimestamp(); }
                maxGap = Math.Max(maxGap, Stopwatch.GetElapsedTime(lastAt).TotalMilliseconds);
            }
            var busyDrops = Drops() - busyFrom;
            var busySeconds = rig.TimePos - busyT0;
            var ok = builds >= 2 && tiles >= 2 && busyDrops <= quietDrops && maxGap < 500 && busySeconds > 2.0;
            return CheckResult.From(ok, "play.live-trickplay", "Playback is undisturbed while trickplay builds",
                $"{builds} builds, {tiles} tiles during {F(busySeconds, "0.0")} s of playback; dropped frames {busyDrops} (quiet baseline {quietDrops} in {F(quietSeconds, "0.0")} s); longest position gap {F(maxGap)} ms",
                number: busyDrops, unit: "frames");
        }

        // ---- later phases ---------------------------------------------------------------------------------------------

        private void LaterPhases()
        {
            Add(CheckResult.Skip("later.delete-guard", "Delete path guard", "Phase 3/6"));
        }
    }
}
