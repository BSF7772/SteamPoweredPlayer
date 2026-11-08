// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <param name="Library">A BackgroundRecordPath; null = <c>SPP_REAL_LIBRARY</c>, else Steam's configured one.</param>
/// <param name="Formatter">The player's timecode formatter; null = <see cref="RealCheck.Formatter"/>.</param>
public sealed record RealCheckOptions(string? Library = null, ITimecodeFormatter? Formatter = null);

/// <summary>
/// <c>SteamPoweredPlayer.exe --realcheck &lt;out.txt&gt; [--library &lt;BackgroundRecordPath&gt;]</c> (plan 2026-11-07 §4.3, D-TEST): short checks
/// on the maintainer's <b>real</b> recordings, under 60 s. It uses the SHORTEST saved clip and the last 10 s of the ACTIVE (else newest)
/// cache session, with a real libmpv rendering into a hidden window, and checks: open → first frame, the UI axis (0 at the head, UI
/// duration = index duration), +5 s / −5 s within one frame, frame-step = one frame, the A-B loop, audio within 0.2 s of the first
/// frame, markers inside the timeline, the timecode format (F2/F3), RAM against D-RAM and the leak slope. <b>Read-only</b> on the library:
/// the data folder is a temp directory that is deleted at the end. The report carries counts and numbers only (no paths, names, ids).
/// </summary>
public static class RealCheck
{
    /// <summary>The app registers the Player page's formatter here (agent B, F2/F3). Null: the timecode case reports SKIP.</summary>
    public static ITimecodeFormatter? Formatter { get; set; }

    public static async Task<int> RunAsync(string reportPath, RealCheckOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        var report = new DiagnosticReport("realcheck", "SteamPoweredPlayer --realcheck (real library, read-only)");
        var sw = Stopwatch.StartNew();
        var runner = new Runner(report, reportPath, options.Formatter ?? Formatter);
        try { await runner.RunAsync(options, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            report.Add(CheckResult.Fail("realcheck.crash", "Realcheck crashed", "exception", Redactor.Describe(e, runner.Root)));
            report.Abort("unexpected exception");
        }
        report.Add(CheckResult.Info("realcheck.elapsed", "Elapsed", BudgetRules.F(sw.Elapsed.TotalSeconds) + " s", number: sw.Elapsed.TotalSeconds, unit: "s"));
        report.WriteTo(reportPath);
        return report.ExitCode;
    }

    private sealed class Runner(DiagnosticReport report, string reportPath, ITimecodeFormatter? formatter)
    {
        public string? Root;
        private string _temp = "";
        private LibraryService? _svc;

        private static string F(double v, string f = "0.#") => BudgetRules.F(v, f);
        private static string Ms(double seconds) => F(seconds * 1000, "0") + " ms";

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
                Add(CheckResult.Fail(id, name, "exception", Redactor.Describe(e, Root)));
            }
        }

        public async Task RunAsync(RealCheckOptions options, CancellationToken ct)
        {
            report.AddHeader("Hardware: " + HardwareClass.Detect().Describe());
            var (root, source, _, _) = await Bench.ResolveLibraryAsync(options.Library, ct).ConfigureAwait(false);
            report.AddHeader("Library source: " + source);
            if (root is null)
            {
                Add(CheckResult.Fail("realcheck.library", "Library found", source));
                report.Abort("no library; pass --library <BackgroundRecordPath> or set SPP_REAL_LIBRARY");
                return;
            }
            Root = Path.GetFullPath(root);
            DiagnosticsEnvironment.PrepareMpv();
            if (!MpvLibrary.IsAvailable())
            {
                Add(CheckResult.Fail("env.libmpv", "libmpv loads", "libmpv-2.dll not found"));
                report.Abort("libmpv is required");
                return;
            }

            _temp = Path.Combine(Path.GetTempPath(), "spp-realcheck-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_temp);
            try
            {
                var data = DataFolder.Open(Path.Combine(_temp, "data"), Root);
                data.EnsureCreated();
                _svc = new LibraryService(new LibraryServiceOptions { RecordingRoot = Root, DataFolder = data });
                var sw = Stopwatch.StartNew();
                await _svc.StartAsync(ct).ConfigureAwait(false);
                await _svc.WhenIdleAsync(ct).ConfigureAwait(false);
                var all = _svc.Index.Recordings;
                Add(CheckResult.Info("realcheck.scan", "Library scanned (temp data folder)",
                    $"{all.Count} recordings ({all.Count(r => r.Kind == RecordingKind.Clip)} clips, {all.Count(r => r.Kind == RecordingKind.CacheSession)} cache sessions) in {F(sw.Elapsed.TotalMilliseconds, "0")} ms"));

                var clip = RealCheckRules.ShortestClip(all);
                var cache = RealCheckRules.ActiveCache(all);

                if (cache is not null && _svc.GetMedia(cache.RelativeFolder) is { } cm)
                    await Step("ram", "RAM (D-RAM)", () => RamSection(cm, ct)).ConfigureAwait(false);
                else Add(CheckResult.Skip("ram", "RAM (D-RAM)", "no cache session in the library"));

                if (clip is not null && _svc.GetMedia(clip.RelativeFolder) is { } clipMedia)
                    await Step("clip", "Shortest saved clip", () => RecordingSection("clip", "shortest saved clip", clip, clipMedia, tail: false, ct)).ConfigureAwait(false);
                else Add(CheckResult.Skip("clip", "Shortest saved clip", $"no playable saved clip of at least {F(RealCheckRules.MinClipSeconds)} s"));

                if (cache is not null && _svc.GetMedia(cache.RelativeFolder) is { } cacheMedia)
                    await Step("cache", "Cache session, last 10 s", () => RecordingSection("cache", (cache.Live ? "active" : "newest") + " cache session, last 10 s", cache, cacheMedia, tail: true, ct)).ConfigureAwait(false);
                else Add(CheckResult.Skip("cache", "Cache session, last 10 s", "no cache session in the library"));

                await Step("gap", "Seek into a gap", () => GapSection(all, ct)).ConfigureAwait(false);
                await Step("markers.library", "Markers, whole library", () => MarkerSweep(all, ct)).ConfigureAwait(false);
                TimecodeSection();
            }
            finally
            {
                if (_svc is not null) await _svc.DisposeAsync().ConfigureAwait(false);
                try { Directory.Delete(_temp, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        // ---- rig -------------------------------------------------------------------------------------------------------

        /// <summary>Hardware decode into mpv's D3D11 renderer on a hidden window (what the app does), else software.</summary>
        private static PlaybackRig CreateRig()
        {
            try { return PlaybackRig.Create(DecodeMode.Gpu, renderHidden: true); }
            catch (Exception e) when (e is MpvException or InvalidOperationException) { return PlaybackRig.Create(DecodeMode.Cpu); }
        }

        private static double Fps(PlaybackRig rig)
        {
            foreach (var p in new[] { "container-fps", "estimated-vf-fps" })
            {
                var v = rig.Player.GetDouble(p);
                if (double.IsFinite(v) && v is > 1 and < 1000) return v;
            }
            return 60;
        }

        private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, int pollMs = 10)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (condition()) return true;
                await Task.Delay(pollMs).ConfigureAwait(false);
            }
            return condition();
        }

        // ---- one recording ---------------------------------------------------------------------------------------------

        private async Task RecordingSection(string id, string label, RecordingEntry entry, SessionMedia media, bool tail, CancellationToken ct)
        {
            report.AddHeader(string.Create(CultureInfo.InvariantCulture,
                $"{char.ToUpperInvariant(label[0])}{label[1..]}: {media.ChunkCount} chunks ({F(media.DurationSeconds)} s), {media.Runs.Count} run(s), axis shift {(media.AxisShiftSeconds == 0 ? "0" : "> 0")}, live {media.Live}"));
            var mediaStart = media.TfdtOriginSeconds;
            var start = tail ? PlaybackSession.Snap(media, media.ChunkEndSeconds(media.LastChunk!.Value) - RealCheckRules.CacheTailSeconds) : mediaStart;

            await using var rig = CreateRig();
            var s = rig.Session;
            await rig.PreWarmAsync(media, ct).ConfigureAwait(false);

            // open -> first frame, playing (the audio check needs playback)
            var sw = Stopwatch.StartNew();
            await s.OpenAsync(media, start, play: true, ct).ConfigureAwait(false);
            var openMs = sw.Elapsed.TotalMilliseconds;
            var t0 = rig.TimePos;
            Add(CheckResult.From(double.IsFinite(t0), $"{id}.open", $"{label}: open -> first frame", $"{F(openMs, "0")} ms, hwdec-current {rig.HwdecCurrent}",
                number: openMs, unit: "ms"));
            if (!double.IsFinite(t0)) return;

            await AudioCheck(id, label, rig, start, ct).ConfigureAwait(false);
            var fps = Fps(rig);

            // the UI axis, paused
            s.SetPause(true);
            await s.SeekAsync(start, ct).ConfigureAwait(false);
            var mpvUi = rig.TimePos - s.MediaStartSeconds;
            var expectUi = start - mediaStart;
            Add(CheckResult.From(RealCheckRules.WithinFrames(expectUi, mpvUi, fps) && RealCheckRules.WithinFrames(expectUi, s.UiPosition, fps), $"{id}.ui-position",
                $"{label}: UI position {(tail ? "at the tail" : "starts at 0")}",
                $"mpv time-pos - media start = {F(mpvUi, "0.000")} s, session UiPosition {F(s.UiPosition, "0.000")} s, expected {F(expectUi, "0.000")} s ({F(fps, "0.##")} fps)"));
            var indexSpan = entry.Runs.Count == 0 ? 0 : media.ChunkEndSeconds(entry.Runs[^1].Last) - media.ChunkStartSeconds(entry.Runs[0].First);
            var expectedDuration = media.Runs.Count > 1 ? indexSpan : entry.DurationSeconds;
            Add(CheckResult.From(RealCheckRules.WithinFrames(expectedDuration, s.UiDuration, fps) || (media.Live && s.UiDuration >= expectedDuration), $"{id}.ui-duration",
                $"{label}: UI duration = index duration",
                $"UiDuration {F(s.UiDuration, "0.000")} s, index {F(expectedDuration, "0.000")} s" + (media.Live ? " (live: may only grow)" : "")));

            // +5 s / -5 s
            async Task<(double Expected, double Landed)> Jump(double delta)
            {
                var expected = PlaybackSession.Snap(media, s.Position + delta);
                await s.SeekRelativeAsync(delta, ct).ConfigureAwait(false);
                return (expected, rig.TimePos);
            }
            var fwd = await Jump(5).ConfigureAwait(false);
            var back = await Jump(-5).ConfigureAwait(false);
            var jumpOk = RealCheckRules.WithinFrames(fwd.Expected, fwd.Landed, fps) && RealCheckRules.WithinFrames(back.Expected, back.Landed, fps)
                         && fwd.Expected - start > 4.9;
            Add(CheckResult.From(jumpOk, $"{id}.jump5", $"{label}: +5 s / -5 s land within 1 frame",
                $"+5: UI {F(fwd.Expected - mediaStart, "0.000")} s expected, landed {F(fwd.Landed - mediaStart, "0.000")} s; -5: expected {F(back.Expected - mediaStart, "0.000")} s, landed {F(back.Landed - mediaStart, "0.000")} s"));

            // frame step
            var steps = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var p0 = rig.TimePos;
                s.FrameStep();
                await WaitFor(() => Math.Abs(rig.TimePos - p0) > 1e-6, TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                steps.Add(rig.TimePos - p0);
            }
            var frame = 1 / fps;
            var stepOk = steps.All(d => Math.Abs(d - frame) <= frame / 2 + 0.001);
            Add(CheckResult.From(stepOk, $"{id}.frame-step", $"{label}: frame-step = 1 frame",
                $"steps {string.Join(", ", steps.Select(d => Ms(d)))} (1 frame = {Ms(frame)} at {F(fps, "0.##")} fps)"));

            // A-B loop: A = 1 s, B = 3 s after the opened point (the clip: UI 1 s and 3 s)
            var a = start + 1;
            var b = start + 3;
            s.SetLoop(a, b);
            await s.SeekAsync(a, ct).ConfigureAwait(false);
            s.SetPause(false);
            var samples = new List<double>();
            var loop = Stopwatch.StartNew();
            while (loop.Elapsed < TimeSpan.FromSeconds(4.6))
            {
                await Task.Delay(15, ct).ConfigureAwait(false);
                samples.Add(rig.TimePos);
            }
            s.SetPause(true);
            s.SetLoop(null, null);
            var ab = RealCheckRules.EvaluateAbLoop(samples, a, b, fps);
            Add(CheckResult.From(ab.Ok, $"{id}.ab-loop", $"{label}: A-B loop (A = 1 s, B = 3 s) loops", ab.Describe()));

            await LoopLengthCheck(id, label, rig, media, start, fps, ct).ConfigureAwait(false);
            await FrameBackCheck(id, label, rig, media, start, fps, ct).ConfigureAwait(false);
            await MarkerCheck(id, label, entry, media, ct).ConfigureAwait(false);
        }

        /// <summary>P1: the loop is as long as the marked range, measured from time-pos over at least three laps.</summary>
        private async Task LoopLengthCheck(string id, string label, PlaybackRig rig, SessionMedia media, double start, double fps, CancellationToken ct)
        {
            var s = rig.Session;
            var chunk = media.SegmentSeconds;
            // inside one chunk, and spanning two chunk boundaries' worth of range (>= 2 chunks), at 1x and 2x
            var cases = new (string Name, double A, double B, double Speed)[]
            {
                ("one chunk, 1x", start + 0.5, start + 2.5, 1),
                ("two chunks, 1x", start + chunk * 0.5, start + chunk * 1.5 + 1.5, 1),
                ("two chunks, 2x", start + chunk * 0.5, start + chunk * 1.5 + 1.5, 2),
            };
            foreach (var (name, a0, b0, speed) in cases)
            {
                var a = PlaybackSession.Snap(media, a0);
                // B inside the last chunk must stay before its last frame: a loop ended by the end of the file is a shorter lap by design
                var b = PlaybackSession.Snap(media, Math.Min(b0, media.ChunkEndSeconds(media.LastChunk!.Value) - 0.25));
                if (b - a < 1) continue;
                s.SetPause(true);
                s.SetSpeed(speed);
                s.SetLoop(a, b);
                await s.SeekAsync(a, ct).ConfigureAwait(false);
                s.SetPause(false);
                var samples = new List<double>();
                var sw = Stopwatch.StartNew();
                var run = TimeSpan.FromSeconds(Math.Min(40, (b - a) / speed * 4.6 + 1.5));   // a partial lap plus three full ones
                const int stepMs = 15;
                while (sw.Elapsed < run)
                {
                    await Task.Delay(stepMs, ct).ConfigureAwait(false);
                    samples.Add(rig.TimePos);
                }
                s.SetPause(true);
                s.SetLoop(null, null);
                s.SetSpeed(1);
                var r = RealCheckRules.EvaluateLoopLength(samples, a, b, fps, speed, stepMs / 1000.0);
                var key = name.Replace(", ", "-").Replace(" ", "");
                Add(CheckResult.From(r.Ok, $"{id}.loop-length.{key}", $"{label}: A-B loop length ({name}, {F(b - a, "0.0")} s) matches A→B",
                    r.Describe(), number: double.IsFinite(r.MaxSpanError) ? r.MaxSpanError * 1000 : double.NaN, unit: "ms"));
            }
        }

        /// <summary>P2: ten consecutive frame-backs, the first across a chunk boundary, each exactly one frame, timecodes agreeing.</summary>
        private async Task FrameBackCheck(string id, string label, PlaybackRig rig, SessionMedia media, double start, double fps, CancellationToken ct)
        {
            var s = rig.Session;
            var frame = 1 / fps;
            var mediaStart = media.TfdtOriginSeconds;
            // 4 frames after the first chunk boundary inside the opened range: 10 steps cross it
            var from = PlaybackSession.Snap(media, start + media.SegmentSeconds + 4 * frame);
            s.SetPause(true);
            await s.SeekAsync(from, ct).ConfigureAwait(false);
            await Task.Delay(250, ct).ConfigureAwait(false);
            var positions = new List<double> { rig.TimePos };
            for (var i = 0; i < 10; i++)
            {
                await s.StepBackAsync(frame, ct).ConfigureAwait(false);
                await Task.Delay(250, ct).ConfigureAwait(false);
                positions.Add(rig.TimePos);
            }
            var r = RealCheckRules.EvaluateFrameBacks(positions, fps);
            var perSecond = (int)Math.Round(fps);
            var tcOk = true;
            string tcNote = "";
            if (formatter is { } f)
            {
                // Steam's pts are irregular (a real clip: 15-21 ms gaps at 59.094 fps), so a fixed-fps FF grid can repeat or skip one
                // number for a genuine one-frame step. The timecode must agree with the shown frame within that rounding: it never rises,
                // each step drops 0..2 FF, and the ten steps together drop 10 FF +-1.
                var tcs = positions.Select(p => f.Format(p - mediaStart, fps)).ToList();
                long Index(string tc) =>
                    tc.Length >= 11 && int.TryParse(tc[0..2], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                    && int.TryParse(tc[3..5], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                    && int.TryParse(tc[6..8], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                    && int.TryParse(tc[9..11], NumberStyles.None, CultureInfo.InvariantCulture, out var ff)
                        ? ((h * 60L + m) * 60 + s) * perSecond + ff - 1 : long.MinValue;
                var idx = tcs.Select(Index).ToList();
                if (idx.Any(i => i == long.MinValue)) tcOk = false;
                else
                {
                    for (var i = 1; i < idx.Count; i++)
                    {
                        var d = idx[i - 1] - idx[i];
                        if (d < 0 || d > 2) tcOk = false;
                    }
                    if (Math.Abs(idx[0] - idx[^1] - (idx.Count - 1)) > 1) tcOk = false;
                }
                tcNote = (tcOk ? "; timecodes agree with the shown frames (steps of 0-2 FF, 10 FF +-1 in total)" : "; timecodes do NOT agree with the shown frames") + " [" + string.Join(" ", tcs.Select(t => t.Length >= 11 ? t[^11..] : t)) + "] fps " + fps.ToString("0.###", CultureInfo.InvariantCulture);
            }
            Add(CheckResult.From(r.Ok && tcOk, $"{id}.frame-back", $"{label}: 10 frame-backs, each exactly one frame (across a chunk boundary)",
                r.Describe() + tcNote + "; deltas (ms) " + string.Join(", ", positions.Zip(positions.Skip(1), (x, y) => Ms(x - y))),
                number: r.MaxErrorFrames, unit: "frames"));
        }

        private async Task AudioCheck(string id, string label, PlaybackRig rig, double start, CancellationToken ct)
        {
            var codec = rig.Player.GetString("current-tracks/audio/codec");
            double firstAudioAt = double.NaN, lastAv = double.NaN;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(1.2))
            {
                var v = rig.TimePos;
                var a = rig.Player.GetDouble("audio-pts");
                if (double.IsFinite(a) && double.IsFinite(v))
                {
                    lastAv = a - v;
                    if (Math.Abs(lastAv) < 0.1) { firstAudioAt = v - start; break; }
                }
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            var ok = codec is { Length: > 0 } && double.IsFinite(firstAudioAt) && firstAudioAt <= 0.2;
            Add(CheckResult.From(ok, $"{id}.audio", $"{label}: audio within 0.2 s of the first frame",
                codec is null ? "no audio track" :
                double.IsFinite(firstAudioAt) ? $"audio follows the video {Ms(firstAudioAt)} after the first frame (A-V {Ms(lastAv)}, {Redactor.Scrub(codec)})"
                : $"no audio pts near the video within 1.2 s (last A-V {(double.IsFinite(lastAv) ? Ms(lastAv) : "n/a")})"));
        }

        private async Task MarkerCheck(string id, string label, RecordingEntry entry, SessionMedia media, CancellationToken ct)
        {
            var set = await _svc!.LoadMarkersAsync(entry.RelativeFolder, ct).ConfigureAwait(false);
            var place = Markers.Place(set.Markers, media);
            double lo = media.TfdtOriginSeconds, hi = media.LastChunk is { } l ? media.ChunkEndSeconds(l) : lo;
            var outside = place.Visible.Count(m => (m.EndSeconds ?? m.StartSeconds) < lo || m.StartSeconds >= hi);
            var trimmed = place.TrimmedAway.GroupBy(t => t.Reason).ToDictionary(g => g.Key, g => g.Count());
            int T(TrimReason r) => trimmed.GetValueOrDefault(r);
            var indexAgrees = entry.MarkerCount == place.Visible.Count;
            var ok = outside == 0 && (indexAgrees || media.Live);
            Add(CheckResult.From(ok, $"{id}.markers", $"{label}: markers inside the timeline",
                $"{set.Markers.Count} from the timeline ({set.Alignment}), {place.Visible.Count} on the timeline, {outside} outside it; trimmed {T(TrimReason.BeforeStart)} before, {T(TrimReason.AfterEnd)} after, {T(TrimReason.InGap)} in gaps; index says {entry.MarkerCount}"));
        }

        // ---- library-wide markers, gap, timecode -----------------------------------------------------------------------

        private async Task MarkerSweep(IReadOnlyList<RecordingEntry> all, CancellationToken ct)
        {
            int withTimeline = 0, withVisible = 0, visible = 0, allTrimmed = 0, done = 0;
            var sw = Stopwatch.StartNew();
            foreach (var r in all)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(10)) break;
                if (_svc!.GetMedia(r.RelativeFolder) is not { } m) continue;
                var set = await _svc.LoadMarkersAsync(r.RelativeFolder, ct).ConfigureAwait(false);
                done++;
                if (set.Markers.Count == 0) continue;
                withTimeline++;
                var v = Markers.Place(set.Markers, m).Visible.Count;
                visible += v;
                if (v > 0) withVisible++; else allTrimmed++;
            }
            var ok = withTimeline == 0 || visible > 0;
            Add(CheckResult.From(ok, "markers.library", "Markers, whole library (parsed -> placed)",
                $"{done}/{all.Count} recordings read in {F(sw.Elapsed.TotalMilliseconds, "0")} ms: {withTimeline} with timeline markers, {withVisible} with markers on their timeline ({visible} markers), {allTrimmed} with every marker outside their media"));
        }

        private async Task GapSection(IReadOnlyList<RecordingEntry> all, CancellationToken ct)
        {
            var gap = all.Where(r => r.Runs.Count > 1).OrderBy(r => r.DurationSeconds).FirstOrDefault();
            if (gap is null || _svc!.GetMedia(gap.RelativeFolder) is not { Runs.Count: > 1 } media)
            {
                Add(CheckResult.Skip("gap", "Seek into a gap lands on the next run", "no gap session found"));
                return;
            }
            var gapStart = media.ChunkEndSeconds(media.Runs[0].Last);
            var next = media.ChunkStartSeconds(media.Runs[1].First);
            await using var rig = CreateRig();
            await rig.Session.OpenAsync(media, media.TfdtOriginSeconds, play: false, ct).ConfigureAwait(false);
            await rig.Session.SeekAsync((gapStart + next) / 2, ct).ConfigureAwait(false);
            var fps = Fps(rig);
            Add(CheckResult.From(RealCheckRules.WithinFrames(next, rig.TimePos, fps), "gap", "Seek into a gap lands on the next run",
                $"gap {F(next - gapStart)} s; landed {F(rig.TimePos - next, "0.000")} s from the next run's start"));
        }

        private void TimecodeSection()
        {
            if (formatter is null)
            {
                Add(CheckResult.Skip("timecode", "Timecode HH:MM:SS.FF at 30 and 60 fps", "no formatter registered yet (RealCheck.Formatter, agent B)"));
                return;
            }
            foreach (var fps in new[] { 30.0, 60.0 })
            {
                var problems = TimecodeRules.Check(formatter, fps);
                Add(CheckResult.From(problems.Count == 0, $"timecode.{F(fps)}", $"Timecode HH:MM:SS.FF at {F(fps)} fps",
                    problems.Count == 0 ? "00:00:00.01 .. 01:40:00.01, FF 1-based, fixed width" : string.Join("; ", problems.Take(3))));
            }
        }

        // ---- RAM (D-RAM) -----------------------------------------------------------------------------------------------

        private async Task RamSection(SessionMedia media, CancellationToken ct)
        {
            using var proc = Process.GetCurrentProcess();
            double Rss() { proc.Refresh(); return proc.WorkingSet64 / 1048576.0; }
            var end = media.ChunkEndSeconds(media.LastChunk!.Value);
            var start = PlaybackSession.Snap(media, end - RealCheckRules.CacheTailSeconds);

            await using var rig = CreateRig();
            await rig.PreWarmAsync(media, ct).ConfigureAwait(false);
            await rig.Session.OpenAsync(media, start, play: true, ct).ConfigureAwait(false);

            // base: the same process, mpv, decoder and renderer, with a minimal demuxer cache
            rig.Player.SetDemuxerCache(new DemuxerCachePlan(0.5, 1L << 20, 0));
            await rig.Session.ReloadAsync(start, ReloadCause.Policy, true, ct).ConfigureAwait(false);
            var baseSamples = new List<double>();
            for (var i = 0; i < 6; i++) { await Task.Delay(500, ct).ConfigureAwait(false); if (i >= 2) baseSamples.Add(Rss()); }
            var baseMb = baseSamples.Max();

            // soak: the real cache plan, the last 10 s looped by mpv's ab-loop (a seek inside the demuxer cache, no reload): after the
            // first pass the whole tail is cached, so the second pass must be flat.
            var plan = DemuxerCache.For(media);
            rig.Player.SetDemuxerCache(plan);
            await rig.Session.ReloadAsync(start, ReloadCause.Policy, true, ct).ConfigureAwait(false);
            var loopEnd = Math.Max(start + 1, end - 0.5);
            rig.Session.SetLoop(start, loopEnd);
            var xs = new List<double>();
            var ys = new List<double>();
            var net = new List<double>();
            double? cacheFirst = null, cacheLast = null;
            var peak = 0.0;
            var sw = Stopwatch.StartNew();
            var pass = loopEnd - start;
            var warm = pass + 1.5;
            var soak = TimeSpan.FromSeconds(warm + Math.Min(pass, 10));
            var loops = 0;
            var last = rig.TimePos;
            while (sw.Elapsed < soak)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var r = Rss();
                peak = Math.Max(peak, r);
                if (sw.Elapsed.TotalSeconds >= warm)
                {
                    xs.Add(sw.Elapsed.TotalMinutes);
                    ys.Add(r);
                    // what the demuxer cache holds now: growth there is the cache filling, not a leak
                    var cache = rig.Player.GetInt64("demuxer-cache-state/total-bytes") is { } b ? b / 1048576.0 : double.NaN;
                    net.Add(r - cache);
                    if (double.IsFinite(cache)) { cacheFirst ??= cache; cacheLast = cache; }
                }
                var pos = rig.TimePos;
                if (double.IsFinite(pos) && double.IsFinite(last) && last - pos > pass / 2) loops++;
                last = pos;
            }
            rig.Session.SetLoop(null, null);
            var expectedMb = DemuxerCache.ExpectedCacheBytes(media.BitsPerSecond) / 1048576.0;
            var bitrate = media.BitsPerSecond is { } bps ? F(bps / 1e6, "0.0") + " Mbit/s" : "unknown bitrate (fallback " + F(DemuxerCache.FallbackBitsPerSecond / 1e6) + " Mbit/s)";
            Add(BudgetRules.RamGate("ram.peak", "RAM peak <= base + demuxer cache + 15 %", peak, baseMb, expectedMb) with
            {
                Note = $"{bitrate}; {F(sw.Elapsed.TotalSeconds, "0")} s on the cache tail, {loops} loop(s); base measured while playing with a 1 MiB demuxer cache",
            });
            // A slope over ~10 s swings by tens of MB/min with working-set noise alone (measured +3 to +250 MB/min on the same tail), so
            // here it is reported, not gated: the leak gate is --bench's soak (1 or 5 min, D-RAM).
            var rssSlope = BudgetRules.Slope(xs, ys);
            var netSlope = net.All(double.IsFinite) ? BudgetRules.Slope(xs, net) : double.NaN;
            var slope = double.IsFinite(netSlope) ? netSlope : rssSlope;
            Add(CheckResult.Info("ram.slope", "RAM slope, second pass of the tail (indicative; --bench gates the leak)",
                F(slope, "+0.0;-0.0;0.0") + " MB/min",
                $"over {F((xs.Count == 0 ? 0 : xs[^1] - xs[0]) * 60, "0")} s; demuxer cache {(cacheFirst is { } c0 && cacheLast is { } c1 ? F(c0, "0") + " -> " + F(c1, "0") + " MB (subtracted)" : "size not reported by mpv")}"
                + (loops == 0 ? "; the ab-loop did not wrap" : ""), slope, "MB/min"));
        }
    }
}
