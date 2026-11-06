// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S2 follow-up (s2d). Commands:
//   mkcopy    <fixtureDir> <destDir> [--from N] [--last M] [--skip a-b,c] [--anywhere]   copy only
//   copycheck <copyDir> --mpv <exe> [--ref <fixtureDir>] [--hwdec] [--out] [--tag]       head-trim / gap verification, no deletes
//   deltest   <copyDir> --exp 1|2 --mpv <exe> [--delete-cmd <script.ps1>] [...]          live delete experiments (dry run without --delete-cmd)
// This file contains NO call that removes or moves a file. Deletion happens only inside the external script the maintainer passes.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace S2Tool;

public static class Del
{
    static string Hwdec(string[] a) => Program.Opt(a, "--hwdec") ?? "d3d11va";

    // ------------------------------------------------------------------ mkcopy
    public static int MkCopy(string[] a)
    {
        var src = Path.GetFullPath(a[1]); var dst = Path.GetFullPath(a[2]);
        var anywhere = a.Contains("--anywhere");
        if (!anywhere && !Dx.Slash(dst).Contains("/.tools/s2d/", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("refusing: destination must be under .tools/s2d/ (or pass --anywhere)"); return 2; }
        if (Dx.Slash(dst).StartsWith(Dx.Slash(src).TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) || Dx.Slash(dst).TrimEnd('/') == Dx.Slash(src).TrimEnd('/')) { Console.Error.WriteLine("refusing: destination is the source or inside it"); return 2; }
        if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any()) { Console.Error.WriteLine("refusing: destination exists and is not empty"); return 2; }
        int? from = Program.Opt(a, "--from") is { } f ? int.Parse(f, CultureInfo.InvariantCulture) : null;
        int? last = Program.Opt(a, "--last") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : null;
        var skip = Dx.ParseRanges(Program.Opt(a, "--skip")).ToList();
        Directory.CreateDirectory(dst);
        int copied = 0, skipped = 0; long bytes = 0; var others = new List<string>();
        foreach (var p in Directory.EnumerateFiles(src).OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(p);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^chunk-stream([01])-(\d{5})\.m4s$");
            if (m.Success)
            {
                int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if ((from is not null && n < from) || (last is not null && n > last) || skip.Any(e => n >= e.a && n <= e.b)) { skipped++; continue; }
                copied++;
            }
            else others.Add(name);
            File.Copy(p, Path.Combine(dst, name), overwrite: false);
            bytes += new FileInfo(p).Length;
        }
        File.WriteAllText(Path.Combine(dst, Dx.MarkerName),
            $"S2D test copy made by 's2tool mkcopy' on {DateTime.UtcNow:o}.\nfrom={from?.ToString() ?? "-"} last={last?.ToString() ?? "-"} skip={Program.Opt(a, "--skip") ?? "-"}\nOnly folders carrying this file are accepted by 'deltest'.\n");
        Console.WriteLine(JsonSerializer.Serialize(new { dest = Dx.Slash(dst), chunkFilesCopied = copied, chunkFilesSkipped = skipped, otherFiles = others, mebibytes = Dx.R(bytes / 1048576.0, 1) }, Dx.J));
        return 0;
    }

    // ------------------------------------------------------------------ shared
    static async Task<Mpv> StartMpv(string[] a, string outDir, string name)
    {
        var exe = Program.Opt(a, "--mpv") ?? throw new ArgumentException("--mpv");
        Mpv.ExtraArgs = (Program.Opt(a, "--extra") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var m = await Mpv.StartAsync(exe, Hwdec(a), Path.Combine(outDir, name + ".mpv.log"));
        await m.Observe("time-pos");
        return m;
    }

    static async Task<Dictionary<string, object?>> Reload(Mpv m, string edl, double start, double playSec = 3)
    {
        var tpBefore = await m.GetD("time-pos");
        var mk = m.Mark(); var sw = Stopwatch.StartNew();
        string? err = null; double? restartMs = null;
        try
        {
            await m.Cmd("loadfile", edl, "replace", "-1", $"start={Dx.Fs(start)},pause=no");
            await m.WaitEvent("playback-restart", mk, 15);
            restartMs = Dx.R(sw.Elapsed.TotalMilliseconds, 0);
        }
        catch (Exception e) { err = Dx.Clip(e.Message, 200); }
        await Task.Delay(500);
        var tp0 = await m.GetD("time-pos"); var w = Stopwatch.StartNew();
        await Task.Delay((int)(playSec * 1000));
        var tp1 = await m.GetD("time-pos"); var wall = w.Elapsed.TotalSeconds;
        var ends = m.Events("end-file", mk).Select(e => e.Json.TryGetProperty("reason", out var r) ? r.ToString() + (e.Json.TryGetProperty("file_error", out var fe) ? ":" + fe : "") : "?").ToArray();
        return new()
        {
            ["requestedStart"] = Dx.R(start), ["timePosBefore"] = tpBefore is null ? null : Dx.R(tpBefore.Value), ["error"] = err, ["reloadToRestartMs"] = restartMs,
            ["timePosAfterRestart"] = tp0 is null ? null : Dx.R(tp0.Value), ["offsetFromRequestedS"] = tp0 is null ? null : Dx.R(tp0.Value - start),
            ["advancedS"] = tp0 is null || tp1 is null ? null : Dx.R(tp1.Value - tp0.Value), ["wallS"] = Dx.R(wall),
            ["playing"] = tp0 is not null && tp1 is not null && tp1 - tp0 > wall * 0.8, ["endFileEvents"] = ends, ["hwdecCurrent"] = await m.GetS("hwdec-current"),
        };
    }

    // ------------------------------------------------------------------ copycheck (head trim and gap on copies; nothing is removed)
    public static async Task<int> CopyCheck(string[] a)
    {
        var copy = Path.GetFullPath(a[1]);
        var outDir = Program.Opt(a, "--out") ?? "out"; var tag = Program.Opt(a, "--tag") ?? "copycheck";
        Directory.CreateDirectory(outDir);
        var res = new Dictionary<string, object?> { ["command"] = "copycheck", ["utc"] = DateTime.UtcNow.ToString("o") };
        var s = Scanner.Scan(copy, validateHeads: true);
        var chunks = Dx.AllChunks(s); int first = chunks[0], lastC = chunks[^1]; double B = Dx.BaseOf(s);
        res["runs"] = s.Runs.Select(r => new { r.First, r.Count, Last = r.First + r.Count - 1 });
        res["chunksComplete"] = chunks.Count;

        // 1. the media (tfdt) axis is a pure function of the chunk number, so trimming the head cannot move it
        var ts = Mp4.Timescale(Path.Combine(copy, "init-stream0.m4s")) ?? 0;
        var dev = new List<double>(); var tfdt = new Dictionary<int, double>();
        foreach (var n in chunks) if (Mp4.BaseDecodeTime(Dx.ChunkPath(copy, 0, n)) is { } t) { tfdt[n] = (double)t / ts; dev.Add(tfdt[n] - 3.0 * (n - 1)); }
        res["axis"] = new { timescale = ts, tfdtMinusNominalMinS = Dx.R(dev.Min(), 4), tfdtMinusNominalMaxS = Dx.R(dev.Max(), 4), spreadMs = Dx.R((dev.Max() - dev.Min()) * 1000, 2), firstChunkTfdtS = Dx.R(tfdt[first], 4), leadBaseB = B };
        if (Program.Opt(a, "--ref") is { } rf)
        {
            int cmp = 0, bad = 0, sizeBad = 0;
            foreach (var n in chunks)
            {
                var rp = Dx.ChunkPath(Path.GetFullPath(rf), 0, n); if (!File.Exists(rp)) continue; cmp++;
                if (Mp4.BaseDecodeTime(rp) != Mp4.BaseDecodeTime(Dx.ChunkPath(copy, 0, n))) bad++;
                if (new FileInfo(rp).Length != new FileInfo(Dx.ChunkPath(copy, 0, n)).Length) sizeBad++;
            }
            res["refCompare"] = new { chunksCompared = cmp, tfdtMismatches = bad, sizeMismatches = sizeBad, note = "same chunk number => same tfdt as the untrimmed fixture, so markers/offsets (defined on the tfdt axis) are unaffected" };
        }

        var edl = Dx.WriteLeadEdl(s, Path.Combine(outDir, $"copycheck-{tag}.edl"));
        res["edl"] = new { path = edl, parts = chunks.Count * 2, firstPartDeclaredS = 3.0 * (first - 1) + 3.0 * ((s.Runs[0].Count > 0 && s.Runs.Count > 1 ? s.Runs[0].Count : 1)) };
        using var m = await StartMpv(a, outDir, $"copycheck-{tag}");
        var log = new Dx.LogReader(Path.Combine(outDir, $"copycheck-{tag}.mpv.log"));
        var steps = new List<Dictionary<string, object?>>(); res["steps"] = steps;
        async Task<Dictionary<string, object?>> Step(string label, double start, double playSec, double expectTpMin, double expectTpMax)
        {
            var r = await Reload(m, edl, start, playSec); r["label"] = label;
            var tp0 = r["timePosAfterRestart"] as double?;
            r["tpInExpectedWindow"] = tp0 is not null && tp0 >= expectTpMin && tp0 <= expectTpMax;
            r["playheadChunk"] = tp0 is null ? null : Dx.ChunkAt(tp0.Value);
            steps.Add(r); return r;
        }
        // 2. (time-pos is read ~0.5 s after playback-restart, hence the +1.0 s window) play from the first remaining chunk, then seek-by-reload (jump ahead, back to the first chunk = revisit, to the last chunk)
        var f0 = tfdt[first];
        await Step("open at first remaining chunk", f0, 4, f0 - 0.2, f0 + 1.0);
        int mid = chunks[Math.Min(5, chunks.Count - 1)];
        await Step($"reload into chunk {mid}", tfdt[mid], 3, tfdt[mid] - 0.2, tfdt[mid] + 1.0);
        await Step("reload back into the first remaining chunk (revisit)", f0, 3, f0 - 0.2, f0 + 1.0);
        await Step($"reload into last chunk {lastC} (3 s before its end)", tfdt[lastC] + 0.0, 2.5, tfdt[lastC] - 0.2, tfdt[lastC] + 1.0);
        // 3. gap handling: play across every hole, then reload just after it
        var gaps = new List<Dictionary<string, object?>>(); res["gaps"] = gaps;
        for (int i = 0; i + 1 < s.Runs.Count; i++)
        {
            var endRun = s.Runs[i].First + s.Runs[i].Count - 1; var nextFirst = s.Runs[i + 1].First;
            double startAt = Math.Max(tfdt[s.Runs[i].First], tfdt[endRun] - 1.5);          // 1.5 s before the hole
            double hole = 3.0 * (nextFirst - endRun - 1);
            var tr = new Dx.Sampler(); using var cts = new CancellationTokenSource(); long t0 = Mpv.Now();
            var mk = m.Mark();
            await m.Cmd("loadfile", edl, "replace", "-1", $"start={Dx.Fs(startAt)},pause=no"); await m.WaitEvent("playback-restart", mk, 15);
            tr.Start(m, t0, cts.Token, 200);
            double seconds = 1.5 + 3.0 + hole * 0 + 6;                                      // 1.5 s tail + the last chunk of the run + into the next run
            await Task.Delay((int)(seconds * 1000)); cts.Cancel(); await tr.Stop();
            var tpEnd = await m.GetD("time-pos");
            var w = tr.Window(0, 1e9);
            var g = new Dictionary<string, object?>
            {
                ["holeAfterChunk"] = endRun, ["missingChunks"] = nextFirst - endRun - 1, ["holeSeconds"] = hole, ["playStartTfdt"] = Dx.R(startAt), ["timePosAtEnd"] = tpEnd is null ? null : Dx.R(tpEnd.Value),
                ["reachedNextRun"] = tpEnd is not null && Dx.ChunkAt(tpEnd.Value) >= nextFirst, ["continuity"] = w,
            };
            g["reloadAfterGap"] = await Reload(m, edl, tfdt[nextFirst], 3);
            gaps.Add(g);
            tr.WriteCsv(Path.Combine(outDir, $"copycheck-{tag}-gap{endRun}.csv"));
        }
        res["mpvLog"] = log.Digest(log.Poll());
        var ok = steps.All(x => x["playing"] is true && x["tpInExpectedWindow"] is true) && gaps.All(x => x["reachedNextRun"] is true);
        res["allChecksPassed"] = ok;
        File.WriteAllText(Path.Combine(outDir, $"copycheck-{tag}.json"), JsonSerializer.Serialize(res, Dx.J));
        Console.WriteLine(JsonSerializer.Serialize(res, Dx.J));
        return ok ? 0 : 1;
    }

    // ------------------------------------------------------------------ deltest
    sealed record Deleted(int Chunk, int Stream, string Path, long Size);

    public static async Task<int> DelTest(string[] a)
    {
        var copy = Path.GetFullPath(a[1]);
        var exp = Program.Opt(a, "--exp") ?? throw new ArgumentException("--exp 1|2");
        var script = Program.Opt(a, "--delete-cmd");
        var outDir = Program.Opt(a, "--out") ?? "out"; var tag = Program.Opt(a, "--tag") ?? (script is null ? "dry" : "live");
        if (exp is not ("1" or "2")) { Console.Error.WriteLine("--exp must be 1 or 2"); return 2; }
        if (!File.Exists(Path.Combine(copy, Dx.MarkerName))) { Console.Error.WriteLine($"refusing: {Dx.MarkerName} not found in the folder. Only copies made by 'mkcopy' are accepted."); return 2; }
        if (script is not null && !File.Exists(script)) { Console.Error.WriteLine("--delete-cmd script not found"); return 2; }
        Directory.CreateDirectory(outDir);
        var name = $"deltest-exp{exp}-{tag}";
        var res = new Dictionary<string, object?> { ["command"] = "deltest", ["experiment"] = exp, ["dryRun"] = script is null, ["deleteCmd"] = script is null ? null : Path.GetFileName(script), ["utc"] = DateTime.UtcNow.ToString("o"), ["hwdec"] = Hwdec(a) };
        if (script is null) Console.WriteLine("DRY RUN: no --delete-cmd given. Every step runs except the delete itself.");
        try { await (exp == "1" ? Exp1(a, copy, script, outDir, name, res) : Exp2(a, copy, script, outDir, name, res)); }
        catch (Exception e) { res["FAILED"] = e.ToString(); Console.Error.WriteLine(e); }
        File.WriteAllText(Path.Combine(outDir, name + ".json"), JsonSerializer.Serialize(res, Dx.J));
        Console.WriteLine(JsonSerializer.Serialize(res, Dx.J));
        return res.ContainsKey("FAILED") ? 1 : 0;
    }

    static string SafeTarget(string copy, int stream, int n)
    {
        var p = Dx.ChunkPath(copy, stream, n);
        if (!Path.GetFullPath(p).StartsWith(Path.GetFullPath(copy).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("target outside the copy");
        return Dx.Slash(p);
    }

    static Dictionary<string, object?> Snapshot(Mpv m, string path)
    {
        var d = Dx.FileState(path);
        int[] h; try { h = ShareProbe.Holders(path); } catch (Exception e) { h = []; d["holdersError"] = Dx.Clip(e.Message, 120); }
        d["holderPids"] = h; d["heldByMpv"] = h.Contains(m.Pid);
        return d;
    }

    static async Task Exp1(string[] a, string copy, string? script, string outDir, string name, Dictionary<string, object?> res)
    {
        int stream = int.Parse(Program.Opt(a, "--stream") ?? "0", CultureInfo.InvariantCulture);
        double warm = double.Parse(Program.Opt(a, "--warm-sec") ?? "20", CultureInfo.InvariantCulture);
        double after = double.Parse(Program.Opt(a, "--after-sec") ?? "10", CultureInfo.InvariantCulture);
        var s = Scanner.Scan(copy, validateHeads: true); double B = Dx.BaseOf(s);
        var edl = Dx.WriteLeadEdl(s, Path.Combine(outDir, name + ".edl"));
        using var m = await StartMpv(a, outDir, name);
        var log = new Dx.LogReader(Path.Combine(outDir, name + ".mpv.log"));
        using var cts = new CancellationTokenSource(); var sampler = new Dx.Sampler(); long t0 = Mpv.Now();
        var mk = m.Mark(); await m.Cmd("loadfile", edl, "replace", "-1", $"start={Dx.Fs(B)},pause=no"); await m.WaitEvent("playback-restart", mk, 15);
        sampler.Start(m, t0, cts.Token);
        await Task.Delay((int)(warm * 1000));

        // pick: the chunk under the playhead if Restart Manager says mpv holds it, else the nearest held one
        var tp = await m.GetD("time-pos") ?? B; int cur = Dx.ChunkAt(tp); var all = Dx.AllChunks(s);
        int? target = null; var tried = new List<string>();
        foreach (var n in new[] { cur, cur - 1, cur + 1, cur - 2, cur + 2, cur - 3 }.Where(all.Contains))
        {
            var p = Dx.ChunkPath(copy, stream, n); var h = ShareProbe.Holders(p); tried.Add($"{n}:{(h.Contains(m.Pid) ? "held" : "not held")}");
            if (h.Contains(m.Pid)) { target = n; break; }
        }
        res["pick"] = new { timePosAtPick = Dx.R(tp), playheadChunk = cur, tried, chosen = target, stream };
        if (target is null) { res["FAILED"] = "no chunk near the playhead is held by mpv"; cts.Cancel(); return; }
        var path = SafeTarget(copy, stream, target.Value); long size = new FileInfo(path).Length;
        long free0 = Dx.FreeBytes(copy);
        res["before"] = new { file = Path.GetFileName(path), sizeBytes = size, state = Snapshot(m, path), freeBytes = free0 };

        double tDel = Mpv.Ms(t0, Mpv.Now());
        var rem = await Dx.InvokeRemover(script, path);
        var post0 = Snapshot(m, path); long free1 = Dx.FreeBytes(copy);
        res["remover"] = rem; res["immediatelyAfter"] = new { state = post0, newOpenAttempt = Dx.TryOpen(path), freeBytes = free1, freeDeltaBytes = free1 - free0 };
        await Task.Delay(1500);
        res["plus1_5s"] = new { state = Snapshot(m, path), freeDeltaBytes = Dx.FreeBytes(copy) - free0, mpvLog = log.Digest(log.Poll()) };
        await Task.Delay((int)(after * 1000));
        double tEnd = Mpv.Ms(t0, Mpv.Now());
        var tpNow = await m.GetD("time-pos");
        res["playbackAfterDelete"] = new
        {
            windowFromDeleteS = Dx.R((tEnd - tDel) / 1000), continuity = sampler.Window(tDel, tEnd), timePosNow = tpNow is null ? (double?)null : Dx.R(tpNow.Value), playheadChunkNow = tpNow is null ? (int?)null : Dx.ChunkAt(tpNow.Value),
            coreIdle = (await m.Get("core-idle"))?.ToString(), hwdec = await m.GetS("hwdec-current"), mpvLog = log.Digest(log.Poll()), state = Snapshot(m, path),
        };
        cts.Cancel(); await sampler.Stop(); sampler.WriteCsv(Path.Combine(outDir, name + "-timepos.csv"));
        res["samplesCsv"] = name + "-timepos.csv";

        // reload 1: the SAME (now stale) EDL, which still names the chunk
        var startAt = tpNow ?? B;
        var r1 = await Reload(m, edl, startAt, 4); r1["state"] = Snapshot(m, path); r1["mpvLog"] = log.Digest(log.Poll());
        // reload 2: EDL rebuilt without the chunk (both tracks of that chunk number)
        var s2 = Scanner.Scan(copy, null, null, [(target.Value, target.Value)], validateHeads: false);
        var edl2 = Dx.WriteLeadEdl(s2, Path.Combine(outDir, name + "-rebuilt.edl"));
        var r2 = await Reload(m, edl2, startAt, 4); r2["state"] = Snapshot(m, path); r2["mpvLog"] = log.Digest(log.Poll());
        res["reloadStaleEdl"] = r1; res["reloadRebuiltEdl"] = r2;

        m.Dispose(); await Task.Delay(1500);
        long free2 = Dx.FreeBytes(copy);
        res["afterMpvClosed"] = new { state = Dx.FileState(path), freeDeltaBytes = free2 - free0, expectedFreedBytesIfGone = size, mpvExited = m.Process.HasExited };
        res["note"] = "freeDelta is noisy (the machine is in use); compare it with sizeBytes only as an order of magnitude. State strings: still-there / listed-but-not-openable (delete-pending) / gone.";
    }

    static async Task Exp2(string[] a, string copy, string? script, string outDir, string name, Dictionary<string, object?> res)
    {
        int ticks = int.Parse(Program.Opt(a, "--ticks") ?? "20", CultureInfo.InvariantCulture);
        double tickSec = double.Parse(Program.Opt(a, "--tick-sec") ?? "3", CultureInfo.InvariantCulture);
        var s = Scanner.Scan(copy, validateHeads: true); double B = Dx.BaseOf(s);
        var order = Dx.AllChunks(s);
        var edl = Dx.WriteLeadEdl(s, Path.Combine(outDir, name + ".edl"));
        using var m = await StartMpv(a, outDir, name);
        var log = new Dx.LogReader(Path.Combine(outDir, name + ".mpv.log"));
        using var cts = new CancellationTokenSource(); var sampler = new Dx.Sampler();
        var mk = m.Mark(); await m.Cmd("loadfile", edl, "replace", "-1", $"start={Dx.Fs(B)},pause=no"); await m.WaitEvent("playback-restart", mk, 15);
        long t0 = Mpv.Now(); sampler.Start(m, t0, cts.Token);
        long free0 = Dx.FreeBytes(copy);
        var deleted = new List<Deleted>(); var rows = new List<Dictionary<string, object?>>(); int logErr = 0, logSuspect = 0;
        for (int k = 1; k <= ticks && k <= order.Count; k++)
        {
            var wait = k * tickSec * 1000 - Mpv.Ms(t0, Mpv.Now()); if (wait > 0) await Task.Delay((int)wait);
            int chunk = order[k - 1]; var tp = await m.GetD("time-pos");
            var row = new Dictionary<string, object?> { ["tick"] = k, ["wallS"] = Dx.R(Mpv.Ms(t0, Mpv.Now()) / 1000), ["chunk"] = chunk, ["timePos"] = tp is null ? null : Dx.R(tp.Value), ["playheadChunk"] = tp is null ? null : Dx.ChunkAt(tp.Value) };
            foreach (var st in new[] { 0, 1 })
            {
                var path = SafeTarget(copy, st, chunk); long size = new FileInfo(path).Length;
                var pre = Snapshot(m, path);
                var rem = await Dx.InvokeRemover(script, path);
                var post = Snapshot(m, path);
                deleted.Add(new Deleted(chunk, st, path, size));
                row[$"stream{st}"] = new { sizeBytes = size, heldByMpvBefore = pre["heldByMpv"], remover = rem, stateAfter = post["status"], listedAfter = post["listed"], newOpen = post["open"] };
            }
            var dg = log.Digest(log.Poll()); logErr += (int)dg["errors"]!; logSuspect += (int)dg["suspect"]!;
            if ((int)dg["suspect"]! > 0) row["mpvLog"] = dg;
            rows.Add(row);
        }
        double tEnd = Mpv.Ms(t0, Mpv.Now()); var tpEnd = await m.GetD("time-pos");
        res["ticks"] = rows;
        long free1 = Dx.FreeBytes(copy);
        res["during"] = new
        {
            deletedChunks = deleted.Count / 2, firstDeleted = deleted[0].Chunk, lastDeleted = deleted[^1].Chunk, continuity = sampler.Window(2000, tEnd),
            timePosFirst = sampler.S.FirstOrDefault(x => x.tp.HasValue).tp, timePosLast = tpEnd is null ? null : (double?)Dx.R(tpEnd.Value),
            axisDriftS = tpEnd is null ? null : (double?)Dx.R((tpEnd.Value - sampler.S.First(x => x.tp.HasValue).tp!.Value) - (tEnd - sampler.S.First(x => x.tp.HasValue).t) / 1000),
            mpvLogErrorLines = logErr, mpvLogSuspectLines = logSuspect, coreIdle = (await m.Get("core-idle"))?.ToString(), freeDeltaBytes = free1 - free0, sumDeletedBytes = deleted.Sum(d => d.Size),
        };
        cts.Cancel(); await sampler.Stop(); sampler.WriteCsv(Path.Combine(outDir, name + "-timepos.csv")); res["samplesCsv"] = name + "-timepos.csv";

        // rebuild the EDL without every chunk we deleted and reload at the current position
        var s2 = Scanner.Scan(copy, null, null, [(deleted.Min(d => d.Chunk), deleted.Max(d => d.Chunk))], validateHeads: false);
        var edl2 = Dx.WriteLeadEdl(s2, Path.Combine(outDir, name + "-rebuilt.edl"));
        var startAt = tpEnd ?? B;
        var rl = await Reload(m, edl2, startAt, 4);
        rl["rebuiltRuns"] = s2.Runs.Select(r => new { r.First, r.Count });
        // blackout = time between the loadfile command and the first restarted frame; continuity = jump relative to where it was playing
        rl["timePosJumpAcrossReloadS"] = rl["timePosAfterRestart"] is double t1 && tpEnd is not null ? Dx.R(t1 - tpEnd.Value) : null;
        res["reloadWithoutDeleted"] = rl;
        int still = 0; var heldList = new List<string>();
        foreach (var d in deleted)
        {
            int[] h = []; try { h = ShareProbe.Holders(d.Path); } catch { }
            if (h.Length > 0) { still++; heldList.Add($"{Path.GetFileName(d.Path)}:{string.Join('/', h)}"); }
        }
        res["handlesAfterReload"] = new { deletedFilesChecked = deleted.Count, stillHeldByAnyProcess = still, held = heldList.Take(20), mpvLog = log.Digest(log.Poll()) };

        m.Dispose(); await Task.Delay(1500);
        var finals = deleted.Select(d => Dx.FileState(d.Path)["status"]?.ToString() ?? "?").GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        long free2 = Dx.FreeBytes(copy);
        res["afterMpvClosed"] = new { states = finals, freeDeltaBytes = free2 - free0, sumDeletedBytes = deleted.Sum(d => d.Size), mpvExited = m.Process.HasExited };
        res["note"] = "State strings: still-there / listed-but-not-openable (delete-pending) / gone. A dry run leaves everything still-there. freeDelta is noisy while the machine is in use.";
    }
}
