// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S2: measurement scenarios driven through mpv's JSON IPC.
//   run basic <session> --mpv <mpv.exe> --hwdec d3d11va|no --out <dir> [--tag name]
//   run gap   <session> ...      (builder-level gap + a phantom chunk that is in the EDL but not on disk)
//   run eof   <session> ...      (EOF -> rebuilt EDL + loadfile start=)
//   run share <copyFolder> ...   (play from a COPY, probe share mode of mpv's open files)
using System.Diagnostics;
using System.Text.Json;

namespace S2Tool;

public static class Scenarios
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = true };
    static bool Lead;     // --lead: EDL axis == tfdt axis (first part declared N0*3 s long)
    static double B;      // seek/start base for the EDL currently loaded (0 in classic mode, (firstChunk-1)*3 in lead mode)
    static double BaseOf(SessionMedia s) => Lead ? 3.0 * (s.Runs[0].First - 1) : 0;
    static string FS(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<int> Run(string[] a)
    {
        var scenario = a[1]; var session = a[2];
        var mpvExe = Program.Opt(a, "--mpv") ?? throw new ArgumentException("--mpv");
        var hwdec = Program.Opt(a, "--hwdec") ?? "d3d11va";
        var outDir = Program.Opt(a, "--out") ?? "out";
        var tag = Program.Opt(a, "--tag") ?? hwdec;
        Directory.CreateDirectory(outDir);
        Lead = a.Contains("--lead");
        Mpv.ExtraArgs = (Program.Opt(a, "--extra") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var res = new Dictionary<string, object?> { ["scenario"] = scenario, ["hwdec"] = hwdec, ["leadEdl"] = Lead, ["utc"] = DateTime.UtcNow.ToString("o") };
        try
        {
            switch (scenario)
            {
                case "basic": await Basic(session, mpvExe, hwdec, outDir, tag, res); break;
                case "gap": await Gap(session, mpvExe, hwdec, outDir, tag, res); break;
                case "eof": await Eof(session, mpvExe, hwdec, outDir, tag, res); break;
                case "crashed": await Crashed(session, mpvExe, hwdec, outDir, tag, res); break;
                case "prewarm": await Prewarm(session, mpvExe, hwdec, outDir, tag, res); break;
                case "coldopen": await ColdOpen(session, mpvExe, hwdec, outDir, tag, res); break;
                case "explore": await Explore(session, mpvExe, hwdec, outDir, tag, res); break;
                case "zero": await Zero(session, mpvExe, hwdec, outDir, tag, res); break;
                case "share": await Share(session, mpvExe, hwdec, outDir, tag, res); break;
                default: Console.Error.WriteLine("unknown scenario"); return 2;
            }
        }
        catch (Exception e) { res["FAILED"] = e.ToString(); Console.Error.WriteLine(e); }
        var path = Path.Combine(outDir, $"{scenario}-{tag}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(res, J));
        Console.WriteLine(JsonSerializer.Serialize(res, J));
        return res.ContainsKey("FAILED") ? 1 : 0;
    }

    static SessionMedia ScanV(string folder, int? first = null, int? last = null, IEnumerable<(int a, int b)>? exclude = null, bool validate = true) =>
        Scanner.Scan(folder, first, last, exclude, validateHeads: validate);

    // ---------- zero: a full-size but zero-filled chunk is in the EDL (what a name-only scanner would produce) ----------
    static async Task Zero(string session, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var s = ScanV(session, validate: false); B = BaseOf(s);     // NAME-ONLY scan: includes the zero-filled 4238/4239 video chunks
        int lastSeg = s.Runs[^1].First + s.Runs[^1].Count - 1;
        res["runsNameOnly"] = s.Runs; res["lastSeg"] = lastSeg;
        var edl = await WriteEdl(s, outDir, $"zero-{tag}.edl");
        using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"zero-{tag}.mpv.log"));
        await m.Observe("time-pos"); await m.Observe("eof-reached");
        var mk0 = m.Mark();
        await Open(m, edl, "pause=yes"); await Task.Delay(400);
        var o = await m.GetD("time-pos") ?? 0; res["origin"] = o; res["duration"] = await m.GetD("duration");
        double endU = (lastSeg - s.Runs[0].First + 1) * 3.0;      // end of timeline in UI seconds
        await m.Cmd("seek", B + endU - 12, "absolute+exact"); await Task.Delay(500);
        await m.Set("pause", false);
        var samples = new List<object>();
        for (int i = 0; i < 14; i++)
        {
            await Task.Delay(1000);
            samples.Add(new { tWallS = i + 1, timePos = R(await m.GetD("time-pos") ?? double.NaN, 3), avsyncMs = R((await m.GetD("avsync") ?? 0) * 1000), eof = (await m.Get("eof-reached"))?.ToString() });
        }
        res["playThroughZeroTail"] = samples;
        res["endFileEvents"] = m.Events("end-file", mk0).Select(e => e.Json.ToString()).ToArray();
        res["logWarnings"] = Lines(Path.Combine(outDir, $"zero-{tag}.mpv.log")).Where(l => l.Contains("[e]") || l.Contains("[w]") || l.Contains("rror") || l.Contains("nvalid")).Take(40).ToArray();
    }

    // ---------- helpers ----------
    static List<string> Lines(string path)   // mpv still has its log open: read with full sharing
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var all = new List<string>(); string? l; while ((l = sr.ReadLine()) is not null) all.Add(l); return all;
    }

    // ---------- crashed: name-only EDL over a crashed session (zero MPD, zero-filled video chunk, leftover .tmp) ----------
    static async Task Crashed(string session, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var nameOnly = ScanV(session, validate: false);
        var validated = ScanV(session, validate: true);
        res["runsNameOnly"] = nameOnly.Runs; res["runsValidated"] = validated.Runs;
        var edl = await WriteEdl(nameOnly, outDir, $"crashed-{tag}.edl");
        using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"crashed-{tag}.mpv.log"));
        var mk = m.Mark();
        await m.Cmd("loadfile", edl, "replace");
        string outcome;
        try { await m.WaitEvent("playback-restart", mk, 8); outcome = "playback-restart (it played)"; }
        catch (TimeoutException) { outcome = "no playback-restart within 8 s"; }
        await Task.Delay(500);
        res["outcome"] = outcome;
        res["endFileEvents"] = m.Events("end-file", mk).Select(e => e.Json.ToString()).ToArray();
        res["idleActive"] = (await m.Get("idle-active"))?.ToString();
        res["logErrors"] = Lines(Path.Combine(outDir, $"crashed-{tag}.mpv.log")).Where(l => l.Contains("][e]") || l.Contains("][w]") || l.Contains("rror") || l.Contains("nvalid") || l.Contains("ailed")).Take(25).ToArray();
    }

    // ---------- prewarm: idle mpv opens a tiny 2-chunk EDL first (app startup), then the user opens the 2 h session ----------
    static async Task Prewarm(string session, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var s = ScanV(session); B = BaseOf(s);
        var edl = await WriteEdl(s, outDir, $"prewarm-{tag}.edl");
        var tiny = ScanV(session, s.Runs[0].First + 500, s.Runs[0].First + 501);
        var edlTiny = await WriteEdl(tiny, outDir, $"prewarm-tiny-{tag}.edl");
        var rows = new List<object>();
        for (int i = 0; i < 5; i++)
        {
            using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"prewarm-{tag}-{i}.mpv.log"));
            await Task.Delay(1500);
            B = BaseOf(tiny); var (_, tinyFf) = await Open(m, edlTiny, "pause=yes"); B = BaseOf(s);
            await Task.Delay(500);
            var (fl, ff) = await Open(m, edl);
            await Task.Delay(1000);
            var mk = m.Mark(); var t0 = Mpv.Now();
            await m.Cmd("seek", B + 3600, "absolute+exact");
            var pr = await m.WaitEvent("playback-restart", mk);
            rows.Add(new { i, tinyFirstFrameMs = tinyFf, afterPrewarm_fileLoadedMs = fl, afterPrewarm_firstFrameMs = ff, seekTo1h_ms = R(Mpv.Ms(t0, pr)) });
        }
        res["prewarm"] = rows;
    }

    // ---------- coldopen: N fresh mpv processes, first loadfile of each (VO + hwdec init included) ----------
    static async Task ColdOpen(string session, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var s = ScanV(session); B = BaseOf(s);
        var edl = await WriteEdl(s, outDir, $"coldopen-{tag}.edl");
        var rows = new List<object>();
        for (int i = 0; i < 5; i++)
        {
            using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"coldopen-{tag}-{i}.mpv.log"));
            await Task.Delay(1500);                                   // window + VO are up, mpv idle (as libmpv is when the app starts)
            var (fl, ff) = await Open(m, edl);
            await Task.Delay(500);
            var (fl2, ff2) = await Open(m, edl);                       // second open in the same process
            rows.Add(new { i, firstOpen_fileLoadedMs = fl, firstOpen_firstFrameMs = ff, secondOpen_firstFrameMs = ff2 });
        }
        res["coldOpen"] = rows;
    }

    // ---------- explore: which time axis do seek and time-pos use? ----------
    static async Task Explore(string session, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var s0 = ScanV(session);
        var s = ScanV(session, s0.Runs[0].First + 1000, s0.Runs[0].First + 1039); B = BaseOf(s);   // 40 chunks from the middle: tfdt origin is large
        var edl = await WriteEdl(s, outDir, $"explore-{tag}.edl");
        using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"explore-{tag}.mpv.log"));
        await m.Observe("time-pos");
        await Open(m, edl, "pause=yes"); await Task.Delay(400);
        var tfdt = (double)Mp4.BaseDecodeTime(Path.Combine(session, $"chunk-stream0-{s.Runs[0].First:00000}.m4s"))! / 1e6;
        res["firstChunkTfdtSeconds"] = tfdt; res["extra"] = Mpv.ExtraArgs;
        res["start"] = await State(m);
        var rows = new List<object>();
        foreach (var x in new double[] { 0, 30, 60, 100, tfdt + 30, tfdt + 60 })
        {
            var mk = m.Mark();
            await m.Cmd("seek", B + x, "absolute+exact");
            await m.WaitEvent("playback-restart", mk); await Task.Delay(250);
            rows.Add(new { seekAbsolute = x, timePos = await m.GetD("time-pos"), eof = (await m.Get("eof-reached"))?.ToString() });
        }
        var mk2 = m.Mark();
        await m.Cmd("seek", 10, "relative+exact"); await m.WaitEvent("playback-restart", mk2); await Task.Delay(250);
        rows.Add(new { seekRelative = 10, timePos = await m.GetD("time-pos") });
        res["seeks"] = rows;
        res["duration"] = await m.GetD("duration");
        res["demuxer-start-time"] = await m.GetD("demuxer-start-time");
        res["logLines"] = Lines(Path.Combine(outDir, $"explore-{tag}.mpv.log")).Where(l => l.Contains("start time") || l.Contains("rebase") || l.Contains("[timeline]") && !l.Contains("source")).Take(12).ToArray();
    }

    static double R(double v, int d = 1) => Math.Round(v, d);
    static double Median(IEnumerable<double> xs) { var s = xs.OrderBy(x => x).ToArray(); return s.Length == 0 ? double.NaN : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2; }

    static async Task<string> WriteEdl(SessionMedia s, string outDir, string name, IEnumerable<int>? phantom = null)
    {
        var p = Path.GetFullPath(Path.Combine(outDir, name)).Replace('\\', '/');
        await File.WriteAllTextAsync(p, Edl.Build(s, phantom, lead: Lead), new System.Text.UTF8Encoding(false));
        return p;
    }

    /// <summary>loadfile and time to the first playback-restart (first frame decoded and queued).</summary>
    static async Task<(double fileLoadedMs, double firstFrameMs)> Open(Mpv m, string edl, string? opts = null)
    {
        if (Lead && B > 0 && (opts is null || !opts.Contains("start="))) opts = (opts is null ? "" : opts + ",") + $"start={FS(B)}";
        var mark = m.Mark();
        var t0 = Mpv.Now();
        if (opts is null) await m.Cmd("loadfile", edl, "replace");
        else await m.Cmd("loadfile", edl, "replace", "-1", opts);
        var fl = await m.WaitEvent("file-loaded", mark);
        var pr = await m.WaitEvent("playback-restart", mark);
        return (R(Mpv.Ms(t0, fl)), R(Mpv.Ms(t0, pr)));
    }

    static async Task<Dictionary<string, object?>> State(Mpv m)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in new[] { "time-pos", "playback-time", "duration", "demuxer-start-time", "avsync", "speed", "audio-delay", "hwdec-current", "pause", "eof-reached",
                                  "estimated-vf-fps", "container-fps", "file-format", "current-demuxer", "frame-drop-count", "decoder-frame-drop-count", "vo-delayed-frame-count" })
        {
            var e = await m.Get(p);
            d[p] = e is null ? null : e.Value.ValueKind == JsonValueKind.Number ? R(e.Value.GetDouble(), 4) : e.Value.ToString();
        }
        return d;
    }

    public static double LastAptsMinusTpMs;   // independent of the avsync property: audio-pts minus time-pos, mean of n samples
    static async Task<double> AvsyncMeanMs(Mpv m, double seconds = 1.0, int n = 10)
    {
        var xs = new List<double>();
        var ap = new List<double>();
        for (int i = 0; i < n; i++) { if (await m.GetD("avsync") is double v) xs.Add(v * 1000); if (await m.GetD("audio-pts") is double a1 && await m.GetD("time-pos") is double t1) ap.Add((a1 - t1) * 1000); await Task.Delay((int)(seconds * 1000 / n)); }
        LastAptsMinusTpMs = ap.Count == 0 ? double.NaN : R(ap.Average(), 1);
        return xs.Count == 0 ? double.NaN : R(xs.Average(), 1);
    }

    static (double cpuMs, long wall, long rss, long priv) Snap(Mpv m)
    {
        m.Process.Refresh();
        return (m.Process.TotalProcessorTime.TotalMilliseconds, Mpv.Now(), m.Process.WorkingSet64, m.Process.PrivateMemorySize64);
    }

    static object CpuOver(Mpv m, (double cpuMs, long wall, long rss, long priv) a, (double cpuMs, long wall, long rss, long priv) b)
    {
        double wallMs = Mpv.Ms(a.wall, b.wall);
        double oneCore = (b.cpuMs - a.cpuMs) / wallMs * 100;
        return new { cpuPercentOfOneCore = R(oneCore, 2), cpuPercentOfAllLogicalCores = R(oneCore / Environment.ProcessorCount, 2), workingSetMB = R(b.rss / 1048576.0), privateMB = R(b.priv / 1048576.0) };
    }

    // ---------- basic ----------
    static async Task Basic(string session, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var s = ScanV(session); B = BaseOf(s);
        var lastSeg = s.Runs[^1].First + s.Runs[^1].Count - 1;
        res["segments"] = s.Runs.Sum(r => r.Count); res["runs"] = s.Runs;
        var edl = await WriteEdl(s, outDir, $"basic-{tag}.edl");
        res["edlBytes"] = new FileInfo(edl).Length;
        using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"basic-{tag}.mpv.log"));
        await m.Observe("time-pos");
        res["mpvVersion"] = await m.GetS("mpv-version");

        // 1) open -> first frame (first = cold file cache, then warm), idle mpv already running like libmpv in the app
        var opens = new List<object>(); var warm = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            var (fl, ff) = await Open(m, edl);
            opens.Add(new { i, fileLoadedMs = fl, firstFrameMs = ff });
            if (i > 0) warm.Add(ff);
            await Task.Delay(700);
        }
        res["open"] = new { runs = opens, warmMedianFirstFrameMs = R(Median(warm)), warmMaxFirstFrameMs = warm.Max() };

        // 2) time-pos origin: open paused, no seeking, then read everything
        await Open(m, edl, "pause=yes");
        await Task.Delay(300);
        var st0 = await State(m);
        res["originPaused"] = st0;
        var origin = st0["time-pos"] is double d0 ? d0 : 0;
        // video pts / audio pts of the first frame via video-frame-info is not exposed for pts; use demuxer-cache-time & demuxer-start-time
        res["demuxerCacheTime"] = await m.GetD("demuxer-cache-time");
        res["videoParams"] = (await m.Get("video-params"))?.ToString();
        res["audioParams"] = (await m.Get("audio-params"))?.ToString();
        res["track-list"] = (await m.Get("track-list"))?.ToString();

        // 3) steady-state playback CPU/RAM at 1x (10 s)
        await m.Set("pause", false);
        await Task.Delay(2000);
        var a0 = Snap(m); await Task.Delay(10000); var a1 = Snap(m);
        res["steady1x_10s"] = CpuOver(m, a0, a1);
        res["steady1x_state"] = await State(m);
        res["steady1x_avsyncMeanMs"] = await AvsyncMeanMs(m);

        // 4) seeks (timeline seconds = origin + U)
        double dur = await m.GetD("duration") ?? s.Runs.Sum(r => r.Count) * 3.0;
        res["durationFromMpv"] = dur;
        var targetsU = new List<double> { 3600, 600, 6900, 120, 3600, 1805.5, 5400.25, 30, 3601, 7100 };
        var rng = new Random(42);
        for (int i = 0; i < 10; i++) targetsU.Add(R(rng.NextDouble() * (dur - 10), 3));
        var seeks = new List<object>(); var seekMs = new List<double>(); var seek3600 = new List<double>();
        foreach (var u in targetsU)
        {
            if (u > dur - 3) continue;
            var target = u;
            var mark = m.Mark(); var t0 = Mpv.Now();
            await m.Cmd("seek", B + target, "absolute+exact");
            var pr = await m.WaitEvent("playback-restart", mark);
            var ms = R(Mpv.Ms(t0, pr)); seekMs.Add(ms); if (Math.Abs(u - 3600) < 0.01) seek3600.Add(ms);
            await Task.Delay(1500);
            var tp = await m.GetD("time-pos") ?? double.NaN;
            var tNow = Mpv.Now();
            var avs = await AvsyncMeanMs(m, 0.5, 5);
            var drift = tp - origin - target - Mpv.Ms(pr, tNow) / 1000.0;
            seeks.Add(new { targetU = u, seekMs = ms, timePosMinusExpectedMs = R(drift * 1000), avsyncMs = avs, aptsMinusTpMs = LastAptsMinusTpMs });
        }
        res["seeks"] = seeks;
        res["seekSummary"] = new { n = seekMs.Count, medianMs = R(Median(seekMs)), maxMs = seekMs.Max(), at3600Ms = seek3600, avsyncMaxAbsMs = seeks.Cast<dynamic>().Max(x => Math.Abs((double)x.avsyncMs)) };

        // 4b) paused exact seek: how close does the first shown frame land to the target? (error = shown frame time - target; <=0 means the frame is before the target)
        await m.Set("pause", true);
        var exact = new List<object>(); var errs = new List<double>();
        var exTargets = new List<double> { 3600.0, 1234.5, 5000.0, 100.0, 99.5, 3.0, 6.5 };
        for (int i = 0; i < 20; i++) exTargets.Add(R(rng.NextDouble() * (dur - 10), 3));
        foreach (var u in exTargets)
        {
            var mark = m.Mark();
            await m.Cmd("seek", B + u, "absolute+exact");
            await m.WaitEvent("playback-restart", mark);
            await Task.Delay(150);
            var tp = await m.GetD("time-pos") ?? double.NaN;
            var err = R((tp - origin - u) * 1000, 2); errs.Add(err);
            exact.Add(new { targetU = u, timePosMinusTargetMs = err });
        }
        res["pausedExactSeek"] = exact;
        res["pausedExactSeekSummary"] = new { n = errs.Count, medianErrMs = R(Median(errs)), minErrMs = errs.Min(), maxErrMs = errs.Max(), within34ms = errs.Count(e => e >= -34 && e <= 34), tooEarlyOver100ms = errs.Count(e => e < -100) };

        // 5) live audio-delay change (no seek, no restart)
        await m.Set("pause", false); await Task.Delay(1500);
        var delayRuns = new List<object>();
        var baseAv = await AvsyncMeanMs(m);
        foreach (var dly in new[] { 0.3, -0.2, 0.0 })
        {
            var mark = m.Mark(); var t0 = Mpv.Now();
            await m.Set("audio-delay", dly);
            var rtt = R(Mpv.Ms(t0, Mpv.Now()), 2);
            await Task.Delay(1500);
            var av = await AvsyncMeanMs(m);
            var readBack = await m.GetD("audio-delay");
            delayRuns.Add(new { audioDelaySec = dly, setRoundTripMs = rtt, readBack, avsyncMeanMsAfter = av, aptsMinusTpMsAfter = LastAptsMinusTpMs, restartEvents = m.CountEvents("playback-restart", mark) });
        }
        res["audioDelay"] = new { baselineAvsyncMs = baseAv, baselineAptsMinusTpMs = LastAptsMinusTpMs, runs = delayRuns };

        // 6) speed 0.25 / 2 / 4 (and 1 again)
        var speeds = new List<object>();
        foreach (var sp in new[] { 0.25, 2.0, 4.0, 1.0 })
        {
            await m.Cmd("seek", B + 900, "absolute+exact");
            await Task.Delay(1000);
            await m.Set("speed", sp);
            await Task.Delay(1000);
            var drops0 = (await m.GetD("frame-drop-count") ?? 0) + (await m.GetD("decoder-frame-drop-count") ?? 0);
            var tp0 = await m.GetD("time-pos") ?? 0; var w0 = Mpv.Now(); var c0 = Snap(m);
            await Task.Delay(4000);
            var tp1 = await m.GetD("time-pos") ?? 0; var w1 = Mpv.Now(); var c1 = Snap(m);
            var drops1 = (await m.GetD("frame-drop-count") ?? 0) + (await m.GetD("decoder-frame-drop-count") ?? 0);
            speeds.Add(new
            {
                speed = sp, achievedRatio = R((tp1 - tp0) / (Mpv.Ms(w0, w1) / 1000), 3), framesDropped = drops1 - drops0,
                avsyncMeanMs = await AvsyncMeanMs(m, 0.5, 5), aptsMinusTpMs = LastAptsMinusTpMs, audioSpeedCorrection = await m.GetD("audio-speed-correction"), videoSpeedCorrection = await m.GetD("video-speed-correction"),
                cpu = CpuOver(m, c0, c1),
            });
        }
        res["speed"] = speeds;
        await m.Set("speed", 1.0);

        // 7) frame-step and frame-back-step
        await m.Cmd("seek", B + 1800, "absolute+exact"); await Task.Delay(800);
        await m.Set("pause", true); await Task.Delay(300);
        var fwd = new List<object>(); var prev = await m.GetD("time-pos") ?? 0;
        for (int i = 0; i < 10; i++)
        {
            var mark = m.Mark(); var t0 = Mpv.Now();
            await m.Cmd("frame-step");
            long ts;
            try { ts = await m.WaitEvent("prop:time-pos", mark, 3, e => e.Json.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.Number && Math.Abs(dd.GetDouble() - prev) > 1e-6); }
            catch (TimeoutException) { fwd.Add(new { i, error = "no time-pos change" }); continue; }
            await Task.Delay(40);
            var tp = await m.GetD("time-pos") ?? prev;
            fwd.Add(new { i, latencyMs = R(Mpv.Ms(t0, ts)), deltaMs = R((tp - prev) * 1000, 2) }); prev = tp;
        }
        var back = new List<object>();
        for (int i = 0; i < 5; i++)
        {
            var mark = m.Mark(); var t0 = Mpv.Now();
            await m.Cmd("frame-back-step");
            long ts;
            try { ts = await m.WaitEvent("playback-restart", mark, 5); }
            catch (TimeoutException) { back.Add(new { i, error = "no restart" }); continue; }
            await Task.Delay(60);
            var tp = await m.GetD("time-pos") ?? prev;
            back.Add(new { i, latencyMs = R(Mpv.Ms(t0, ts)), deltaMs = R((tp - prev) * 1000, 2) }); prev = tp;
        }
        res["frameStep"] = new { forward = fwd, back };
        res["finalState"] = await State(m);
        m.Events("end-file").ForEach(e => { }); // keep events referenced
        res["endFileEvents"] = m.Events("end-file").Select(e => e.Json.ToString()).ToArray();
    }

    // ---------- gap ----------
    static async Task Gap(string session, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var all = ScanV(session);
        int first = all.Runs[0].First;
        int g0 = first + 20, g1 = first + 29;  // 10 missing chunks = 30 s hole
        var gapped = ScanV(session, first, first + 60, [(g0, g1)]); B = BaseOf(gapped);
        res["runs"] = gapped.Runs;
        double Tfdt(int n) => (double)Mp4.BaseDecodeTime(Path.Combine(session, $"chunk-stream0-{n:00000}.m4s"))! / Mp4.Timescale(Path.Combine(session, "init-stream0.m4s"))!.Value;
        res["tfdtSeconds"] = new { lastBeforeGap = Tfdt(g0 - 1), firstAfterGap = Tfdt(g1 + 1) };
        // position of chunk n on the axis the loaded EDL uses (seek / start= axis)
        double Pos(int n) => Lead ? 3.0 * (n - 1) : B + 3.0 * (n - first - (n > g1 ? g1 - g0 + 1 : 0));
        using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"gap-{tag}.mpv.log"));
        await m.Observe("time-pos");

        // A) gap built into the EDL (what a scanner produces): play from 6 s before the gap for 50 s, log when time-pos stalls and resumes
        var edlA = await WriteEdl(gapped, outDir, $"gap-A-{tag}.edl");
        await Open(m, edlA, "pause=yes"); await Task.Delay(300);
        res["A_origin"] = await m.GetD("time-pos");
        await m.Cmd("seek", Pos(g0 - 2), "absolute+exact"); await Task.Delay(300);
        await m.Set("pause", false);
        var tpA = new List<object>(); double? prevTp = null; int stalled = 0, maxStalled = 0;
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(1000);
            var tp = await m.GetD("time-pos") ?? double.NaN;
            if (prevTp is double p && Math.Abs(tp - p) < 0.05) { stalled++; maxStalled = Math.Max(maxStalled, stalled); } else stalled = 0;
            prevTp = tp;
            if (i < 14 || i % 5 == 0) tpA.Add(new { tWallS = i + 1, timePos = R(tp, 3), expectedTfdtAxisAtEnd = (double?)null, eof = (await m.Get("eof-reached"))?.ToString() });
        }
        res["A_playAcrossBuiltInGap"] = tpA;
        res["A_longestFrozenSeconds"] = maxStalled;
        res["A_timePosAfter50s"] = prevTp;
        res["A_avsyncAfter"] = new { avsyncMs = await AvsyncMeanMs(m, 0.5, 5), aptsMinusTpMs = LastAptsMinusTpMs };

        // A2) the app handles the gap itself: when time-pos nears the end of run 1, seek to the start of run 2. Measure the blackout.
        await m.Cmd("seek", Pos(g0 - 2), "absolute+exact"); await Task.Delay(500);
        var endOfRun1 = Pos(g0);                                            // axis position where run 1's last chunk ends
        var mkSkip = m.Mark(); long tSeek = 0; double lastTpBeforeSeek = double.NaN;
        for (int i = 0; i < 4000; i++)
        {
            var tp = await m.GetD("time-pos") ?? 0;
            if (tp >= endOfRun1 + (Lead ? 0 : (m.Process.HasExited ? 0 : 0)) - 0.15 + (Lead ? 0 : (await m.GetD("demuxer-start-time") ?? 0) + (res["A_origin"] is double oo ? oo : 0))) { lastTpBeforeSeek = tp; break; }
            await Task.Delay(5);
        }
        var mk2 = m.Mark(); tSeek = Mpv.Now();
        await m.Cmd("seek", Pos(g1 + 1), "absolute+exact");
        var prSkip = await m.WaitEvent("playback-restart", mk2);
        await Task.Delay(1500);
        res["A2_skipGapBySeek"] = new { timePosBeforeSeek = R(lastTpBeforeSeek, 3), seekToFirstFrameMs = R(Mpv.Ms(tSeek, prSkip)), timePosAfter1_5s = await m.GetD("time-pos"), avsyncMs = await AvsyncMeanMs(m, 0.5, 5), aptsMinusTpMs = LastAptsMinusTpMs };

        // B) EDL lists chunk g0+5 although it is NOT on disk (vanished between scan and open, or trimmed by Steam). Play from 6 s before it.
        var withPhantom = ScanV(session, first, first + 60);
        var edlB = await WriteEdl(withPhantom, outDir, $"gap-B-{tag}.edl", phantom: [first + 70]);   // numbers beyond the scanned range: a missing LAST file
        var edlB2 = await WriteEdl(ScanV(session, first, first + 30, [(first + 20, first + 20)]), outDir, $"gap-B2-{tag}.edl", phantom: [first + 20]);  // missing file in the middle (EDL lists it, disk does not have it)
        var markB = m.Mark();
        B = BaseOf(withPhantom);
        await Open(m, edlB2, "pause=yes"); await Task.Delay(300);
        await m.Cmd("seek", B + 3.0 * 18, "absolute+exact"); await Task.Delay(300);
        await m.Set("pause", false);
        var tpB = new List<object>(); double? prevB = null; int stB = 0, maxStB = 0;
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(1000);
            var tp = await m.GetD("time-pos") ?? double.NaN;
            if (prevB is double p && Math.Abs(tp - p) < 0.05) { stB++; maxStB = Math.Max(maxStB, stB); } else stB = 0;
            prevB = tp;
            if (i < 14 || i % 5 == 0) tpB.Add(new { tWallS = i + 1, timePos = R(tp, 3), eof = (await m.Get("eof-reached"))?.ToString(), idle = (await m.Get("idle-active"))?.ToString() });
        }
        res["B_playAcrossListedButMissingFile"] = tpB;
        res["B_longestFrozenSeconds"] = maxStB;
        res["B_endFileEvents"] = m.Events("end-file", markB).Select(e => e.Json.ToString()).ToArray();
        res["B_logFailures"] = Lines(Path.Combine(outDir, $"gap-{tag}.mpv.log")).Where(l => l.Contains("failed to load segment") || l.Contains("Failed to open") || l.Contains("No such file") || l.Contains("Could not open")).Take(8).ToArray();
    }

    // ---------- eof ----------
    static async Task Eof(string session, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var all = ScanV(session);
        int first = all.Runs[0].First + 100;       // pretend head was already trimmed by 100 chunks
        int liveEnd = first + 39;                    // "live end" at the moment of the first scan: 40 chunks = 120 s
        var s1 = ScanV(session, first, liveEnd);
        var s2 = ScanV(session, first, liveEnd + 60);               // session grew by 60 chunks
        var s3 = ScanV(session, first + 10, liveEnd + 60);          // ... and Steam trimmed 10 head chunks
        var e1 = await WriteEdl(s1, outDir, $"eof-1-{tag}.edl"); var e2 = await WriteEdl(s2, outDir, $"eof-2-{tag}.edl"); var e3 = await WriteEdl(s3, outDir, $"eof-3-{tag}.edl");
        B = BaseOf(s1);
        using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"eof-{tag}.mpv.log"));
        await m.Observe("time-pos"); await m.Observe("eof-reached");

        await Open(m, e1, "pause=yes"); await Task.Delay(300);
        var o1 = await m.GetD("time-pos") ?? 0; var dur1 = await m.GetD("duration");
        res["origin1"] = o1; res["duration1"] = dur1;
        await m.Cmd("seek", B + 120 - 6, "absolute+exact"); await Task.Delay(300);
        var mk = m.Mark();
        await m.Set("pause", false);
        await m.WaitEvent("prop:eof-reached", mk, 20, e => e.Json.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.True);
        var tEof = Mpv.Now();
        var tpAtEof = await m.GetD("time-pos") ?? double.NaN;
        res["timePosAtEof"] = R(tpAtEof, 3);
        res["eofState"] = await State(m);

        string F(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        var ui = Lead ? tpAtEof : tpAtEof - o1;                      // seconds on the EDL axis (what seek / start= use)
        res["uiSecondsAtEof"] = R(ui, 3);

        // A) EOF -> rebuilt EDL (session grew), start= on the EDL axis
        var mk2 = m.Mark(); var t0 = Mpv.Now();
        await m.Cmd("loadfile", e2, "replace", "-1", $"start={F(ui)}");
        var pr = await m.WaitEvent("playback-restart", mk2);
        res["A_continueGrown_loadfileToFirstFrameMs"] = R(Mpv.Ms(t0, pr));
        res["A_continueGrown_eofEventToFirstFrameMs"] = R(Mpv.Ms(tEof, pr));
        var first2 = await m.GetD("time-pos");
        await Task.Delay(3500);
        res["A_continueGrown"] = new { origin2 = await m.GetD("demuxer-start-time"), timePosRightAfter = first2, expectedFirst = R(tpAtEof, 3), timePosAfter3_5s = await m.GetD("time-pos"), avsyncMs = R((await m.GetD("avsync") ?? 0) * 1000), duration2 = await m.GetD("duration"), droppedFrames = await m.GetD("decoder-frame-drop-count") };

        // B) same, but start= given on the time-pos (tfdt) axis, to document that it is NOT that axis
        var mkB = m.Mark();
        await m.Cmd("loadfile", e2, "replace", "-1", $"start={F(tpAtEof)}");
        await m.WaitEvent("playback-restart", mkB); await Task.Delay(500);
        res["B_startOnTfdtAxis"] = new { startGiven = R(tpAtEof, 3), timePos = await m.GetD("time-pos"), eof = (await m.Get("eof-reached"))?.ToString() };

        // C) head trimmed by 10 chunks (30 s) while the viewer is at `ui`: rebuilt EDL has a new origin; keep the position with start = ui - 30
        var mk3 = m.Mark();
        B = BaseOf(s3);
        await m.Cmd("loadfile", e3, "replace", "-1", Lead ? $"pause=yes,start={F(B)}" : "pause=yes");
        await m.WaitEvent("playback-restart", mk3); await Task.Delay(300);
        var o3 = await m.GetD("time-pos") ?? double.NaN;
        res["C_trimmedHead"] = new { origin3 = o3, expectedOrigin = R(o1 + 30, 3), duration3 = await m.GetD("duration") };
        var mk4 = m.Mark();
        await m.Cmd("loadfile", e3, "replace", "-1", $"start={F(Lead ? tpAtEof : ui - 30)}");
        await m.WaitEvent("playback-restart", mk4); await Task.Delay(1500);
        res["C_continueTrimmed"] = new { startGiven = R(ui - 30, 3), timePos = await m.GetD("time-pos"), expectedTimePosApprox = R(tpAtEof + 1.5, 3), avsyncMs = R((await m.GetD("avsync") ?? 0) * 1000) };

        // D) mid-play reload (what a CacheFollower does on every growth/trim): measure the media-time jump and the blackout
        await m.Cmd("seek", B + 50, "absolute+exact"); await Task.Delay(1500);
        var tpBefore = await m.GetD("time-pos") ?? 0; var tBefore = Mpv.Now();
        var uiNow = Lead ? tpBefore : tpBefore - o3;
        var mk5 = m.Mark(); var t5 = Mpv.Now();
        await m.Cmd("loadfile", e3, "replace", "-1", $"start={F(uiNow)}");
        var pr5 = await m.WaitEvent("playback-restart", mk5);
        var tpAfter = await m.GetD("time-pos") ?? 0; var tAfterRead = Mpv.Now();
        var lastBefore = m.Events("prop:time-pos", 0).Where(e => e.Ts < t5).LastOrDefault();
        await Task.Delay(1500);
        res["D_midPlayReload"] = new
        {
            loadfileToFirstFrameMs = R(Mpv.Ms(t5, pr5)),
            lastTimePosEventBeforeMsAgo = lastBefore is null ? null : (double?)R(Mpv.Ms(lastBefore.Ts, t5)),
            mediaJumpMs = R((tpAfter - tpBefore - Mpv.Ms(tBefore, tAfterRead) / 1000) * 1000, 1), // media-time progress minus wall-clock time between the two reads
            timePosBefore = R(tpBefore, 3), timePosFirstAfter = R(tpAfter, 3), avsyncMsAfter = R((await m.GetD("avsync") ?? 0) * 1000),
        };
    }

    // ---------- share ----------
    static async Task Share(string copy, string exe, string hwdec, string outDir, string tag, Dictionary<string, object?> res)
    {
        var s = ScanV(copy); B = BaseOf(s);
        var edl = await WriteEdl(s, outDir, $"share-{tag}.edl");
        using var m = await Mpv.StartAsync(exe, hwdec, Path.Combine(outDir, $"share-{tag}.mpv.log"));
        await m.Observe("time-pos");
        await Open(m, edl);
        await Task.Delay(1500);
        var sn = new List<object>();
        string Probe(string phase)
        {
            int held = 0, nd = 0, nw = 0, nr = 0; var names = new List<string>();
            foreach (var f in Directory.EnumerateFiles(copy))
            {
                if (!ShareProbe.Holders(f).Contains(m.Pid)) continue;
                held++; names.Add(Path.GetFileName(f));
                if (ShareProbe.Probe(f, 0x10000) != "ok") nd++;
                if (ShareProbe.Probe(f, 0x2) != "ok") nw++;
                if (ShareProbe.Probe(f, 0x1) != "ok") nr++;
            }
            sn.Add(new { phase, heldByMpv = held, deniedDeleteAccess = nd, deniedWriteAccess = nw, deniedReadAccess = nr, files = names.Take(12) });
            return phase;
        }
        Probe("playing at start");
        await m.Cmd("seek", B + 60, "absolute+exact"); await Task.Delay(1500); Probe("after seek to 1:00");
        await m.Cmd("seek", B + 200, "absolute+exact"); await Task.Delay(1500); Probe("after seek to 3:20");
        await m.Set("pause", true); await Task.Delay(500); Probe("paused");
        await m.Set("pause", false); await m.Cmd("seek", B + 0, "absolute+exact"); await m.Set("speed", 4.0);
        for (int i = 1; i <= 8; i++) { await Task.Delay(5000); Probe($"sequential 4x play, t={i * 5}s (media pos {await m.GetD("time-pos"):0}s)"); }
        res["probes"] = sn;
        res["note"] = "deniedDeleteAccess = files mpv holds WITHOUT FILE_SHARE_DELETE (a DELETE-access open with all-share failed with a sharing violation). Nothing was deleted or modified.";
        // mpv's own log lines about the files it opened
        res["mpvLogOpenLines"] = Lines(Path.Combine(outDir, $"share-{tag}.mpv.log")).Where(l => l.Contains("Opening ") || l.Contains("[file]") || l.Contains("[mp4")).Take(30).ToArray();
    }
}
