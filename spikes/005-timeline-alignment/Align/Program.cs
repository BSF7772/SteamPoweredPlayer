// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S5: survey timelines, map markers into a session, extract frames around them (ffmpeg on byte-concatenated chunks).
// Throwaway code. <RecordingRoot> is opened read-only (chunks are read with delete-sharing and copied into out/cache first).
//
//   Align survey  <RecordingRoot>
//   Align markers <sessionDir> <timelineJson> [iconFilter,...]
//   Align sheet   <sessionDir> <timelineJson> <outName> <ids|auto:N> [span=2] [step=0.5] [shift=0] [icons=death,kill]
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var inv = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;
string outDir = Path.Combine(Directory.GetCurrentDirectory(), "out");   // run from the spike folder

return args.Length == 0 ? Usage() : args[0] switch
{
    "survey" => Survey(args[1]),
    "markers" => Markers(args[1], args[2], args.Length > 3 ? args[3] : "death,kill"),
    "sheet" => Sheet(args),
    "pb" => PbDump(args[1]),
    "clips" => Clips(args[1]),
    "offsets" => Offsets(args[1]),
    _ => Usage()
};

static int Usage() { Console.WriteLine("survey|markers|sheet|pb"); return 1; }

// clip.pb field 1.5 = {1 bg folder, 2 start_ms, 3 duration_ms, 10 bgStartOffsetMs}; list kill-like markers inside each clip window
static int Clips(string root)
{
    foreach (var c in Directory.GetDirectories(Path.Combine(root, "clips"), "clip_730_*"))
    {
        var sw = new StringWriter();
        var b = File.ReadAllBytes(Path.Combine(c, "clip.pb"));
        Pb.Dump(b, 0, b.Length, "", sw);
        var lines = sw.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int k = lines.FindIndex(l => l == "  5: {");
        if (k < 0) continue;
        string bg = ""; long start = 0, dur = 0, off10 = -1;
        for (int i = k + 1; i < lines.Count && lines[i].StartsWith("    "); i++)
        {
            var m = Regex.Match(lines[i], @"^    (\d+): (?:""(.*)""|(\d+))$");
            if (!m.Success) continue;
            switch (m.Groups[1].Value) { case "1": bg = m.Groups[2].Value; break; case "2": start = long.Parse(m.Groups[3].Value); break; case "3": dur = long.Parse(m.Groups[3].Value); break; case "10": off10 = long.Parse(m.Groups[3].Value); break; }
        }
        var tlPath = Directory.GetFiles(Path.Combine(c, "timelines"), "timeline_*.json").First();
        var (tlStart, _, root2) = LoadTimeline(tlPath);
        var bgDir = Path.Combine(c, "video", bg);
        if (!Directory.Exists(bgDir)) { var d = Directory.GetDirectories(Path.Combine(c, "video")).FirstOrDefault(); if (d == null) continue; bgDir = d; }
        var (bgStart, _) = SessionName(bgDir);
        double offName = (bgStart - tlStart).TotalSeconds;
        var ev = Events(root2, new[] { "death", "kill" });
        var f2 = lines.First(l => l.StartsWith("2: ")); double w0 = long.Parse(f2[3..]) / 1000.0, w1 = w0 + dur / 1000.0;
        int inWin = ev.Count(e => e.TimeS >= w0 + 2 && e.TimeS <= w1 - 2); var ids = string.Join(",", ev.Where(e => e.TimeS >= w0 + 2 && e.TimeS <= w1 - 2).Select(e => e.Id));
        Console.WriteLine($"{Path.GetFileName(c)} bg={Path.GetFileName(bgDir)} start_ms={start} dur_ms={dur} off10={off10} offName={offName:F0} chunks={Directory.GetFiles(bgDir, "chunk-stream0-*.m4s").Length} killishInWindow={inWin} w0={w0:F1} ids={ids}");
    }
    return 0;
}

// Library-wide: compare the folder-name offset (bg start - timeline start, whole seconds) with the exact offset Steam stores in
// gamerecording.pb field 1.5 (2 = start offset ms, 10 = same value) for every indexed session whose folder name parses.
static int Offsets(string pbPath)
{
    byte[] b;
    using (var fs = new FileStream(pbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
    using (var ms = new MemoryStream()) { fs.CopyTo(ms); b = ms.ToArray(); }
    var sw = new StringWriter(); Pb.Dump(b, 0, b.Length, "", sw);
    string? tl = null, bg = null; long f2 = -1, f10 = -1; bool inBg = false; var errs = new List<double>(); int n = 0, sameField = 0, dr0 = 0;
    void Flush()
    {
        if (tl != null && bg != null && f10 >= 0)
        {
            var mt = Regex.Match(tl, @"^timeline_(\d+)(\d{8})_(\d{6})$"); var mb = Regex.Match(bg, @"^bg_(\d+)_(\d{8})_(\d{6})(?:_\d+)?$");
            if (mt.Success && mb.Success)
            {
                double nameOff = (ParseUtc(mb.Groups[2].Value, mb.Groups[3].Value) - ParseUtc(mt.Groups[2].Value, mt.Groups[3].Value)).TotalSeconds;
                errs.Add(nameOff - f10 / 1000.0); n++; if (f2 == f10) sameField++;
            }
        }
        tl = bg = null; f2 = f10 = -1; inBg = false;
    }
    foreach (var line in sw.ToString().Split('\n').Select(l => l.TrimEnd('\r')))
    {
        if (line == "1: {") { Flush(); continue; }
        if (line.Length > 0 && line[0] != ' ' && line != "1: {") { Flush(); continue; }   // another top-level field ends the entry
        var m = Regex.Match(line, @"^  1: ""(timeline_[^""]+)""$"); if (m.Success) { tl = m.Groups[1].Value; continue; }
        if (line == "  5: {") { inBg = true; continue; }
        if (inBg && line == "  }") { inBg = false; continue; }
        if (inBg) { var mm = Regex.Match(line, @"^    (\d+): (?:""(.*)""|(\d+))$"); if (!mm.Success) continue;
            if (mm.Groups[1].Value == "1") bg = mm.Groups[2].Value; else if (mm.Groups[1].Value == "2") f2 = long.Parse(mm.Groups[3].Value); else if (mm.Groups[1].Value == "10") f10 = long.Parse(mm.Groups[3].Value); }
    }
    Flush();
    errs.Sort();
    double P(double q) => errs[(int)Math.Min(errs.Count - 1, Math.Floor(q * errs.Count))];
    Console.WriteLine($"sessions={n} field2==field10 for {sameField}");
    Console.WriteLine($"nameOffset - exactOffset (s): min={errs[0]:F3} p5={P(.05):F3} p25={P(.25):F3} median={P(.5):F3} p75={P(.75):F3} p95={P(.95):F3} max={errs[^1]:F3}");
    Console.WriteLine($"|err|>0.5 s: {errs.Count(e => Math.Abs(e) > 0.5)} of {n};  |err|>=1 s: {errs.Count(e => Math.Abs(e) >= 1.0)};  mean={errs.Average():F3}");
    return 0;
}

static int PbDump(string path)
{
    var b = File.ReadAllBytes(path);
    Pb.Dump(b, 0, b.Length, "", Console.Out);
    return 0;
}

static DateTimeOffset ParseUtc(string d, string t) =>
    DateTimeOffset.ParseExact(d + t, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

static (DateTimeOffset start, string gameId) SessionName(string dir)
{
    var m = Regex.Match(Path.GetFileName(dir), @"^bg_(\d+)_(\d{8})_(\d{6})(?:_\d+)?$");   // clips carry an extra "_0" suffix
    if (!m.Success) throw new InvalidOperationException("not a bg folder: " + dir);
    return (ParseUtc(m.Groups[2].Value, m.Groups[3].Value), m.Groups[1].Value);
}

static (DateTimeOffset start, long dateRecorded, JsonElement root) LoadTimeline(string path)
{
    var m = Regex.Match(Path.GetFileName(path), @"^timeline_(\d+)(\d{8})_(\d{6})\.json$");
    var start = ParseUtc(m.Groups[2].Value, m.Groups[3].Value);
    var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    long dr = long.Parse(doc.RootElement.GetProperty("daterecorded").GetString()!, CultureInfo.InvariantCulture);
    return (start, dr, doc.RootElement);
}

static List<Marker> Events(JsonElement root, string[] icons)
{
    var l = new List<Marker>();
    if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return l;
    foreach (var e in entries.EnumerateArray())
    {
        if (e.GetProperty("type").GetString() != "event") continue;
        string icon = e.TryGetProperty("icon", out var i) ? i.GetString() ?? "" : "";
        if (icons.Length > 0 && !icons.Any(k => icon.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
        l.Add(new(e.GetProperty("id").GetString()!, double.Parse(e.GetProperty("time").GetString()!, CultureInfo.InvariantCulture) / 1000.0,
            icon, e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "", e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""));
    }
    return l.OrderBy(m => m.TimeS).ToList();
}

static int Survey(string root)
{
    // every session folder in the library and in clips, matched with the latest same-game timeline that started before it
    var dirs = new List<string>(Directory.GetDirectories(Path.Combine(root, "video"), "bg_*"));
    foreach (var c in Directory.GetDirectories(Path.Combine(root, "clips"), "clip_*"))
    {
        var v = Path.Combine(c, "video");
        if (Directory.Exists(v)) dirs.AddRange(Directory.GetDirectories(v, "bg_*"));
    }
    var tls = new List<(string path, DateTimeOffset start, string gid)>();
    foreach (var f in Directory.GetFiles(Path.Combine(root, "timelines"), "timeline_*.json"))
    {
        var m = Regex.Match(Path.GetFileName(f), @"^timeline_(\d+)(\d{8})_(\d{6})\.json$");
        if (m.Success) tls.Add((f, ParseUtc(m.Groups[2].Value, m.Groups[3].Value), m.Groups[1].Value));
    }
    foreach (var dir in dirs)
    {
        var (start, gid) = SessionName(dir);
        int chunks = Directory.GetFiles(dir, "chunk-stream0-*.m4s").Length;
        var best = tls.Where(t => t.gid == gid && t.start <= start && (start - t.start).TotalSeconds < 1800)
                      .OrderByDescending(t => t.start).FirstOrDefault();
        if (best.path == null) { Console.WriteLine($"{Path.GetFileName(dir)} chunks={chunks} NO-TIMELINE"); continue; }
        var (tlStart, dr, rootEl) = LoadTimeline(best.path);
        var ev = Events(rootEl, new[] { "death", "kill" });
        Console.WriteLine($"{Path.GetFileName(dir)} chunks={chunks} tl={Path.GetFileName(best.path)} off={(start - tlStart).TotalSeconds:F0}s " +
            $"dr==name:{dr == tlStart.ToUnixTimeSeconds()} killish={ev.Count} icons={string.Join(",", ev.Select(e => e.Icon).Distinct())} dir={dir.Replace('\\', '/')}");
    }
    return 0;
}

static int Markers(string sessionDir, string tlPath, string icons)
{
    var (start, _) = SessionName(sessionDir);
    var (tlStart, dr, root) = LoadTimeline(tlPath);
    double off = (start - tlStart).TotalSeconds;
    Console.WriteLine($"session start {start:u} timeline start {tlStart:u} daterecorded {dr} (name {tlStart.ToUnixTimeSeconds()}) off {off}s");
    foreach (var m in Events(root, icons.Split(',', StringSplitOptions.RemoveEmptyEntries)))
        Console.WriteLine($"{m.Id,5} t={m.TimeS,9:F3} pos={m.TimeS - off,9:F3} {m.Icon,-16} {m.Title} | {m.Desc}");
    return 0;
}

double chunk0Val = 0;
int Sheet(string[] a)
{
    string sessionDir = a[1], tlPath = a[2], name = a[3], pick = a[4];
    double span = a.Length > 5 ? double.Parse(a[5], inv) : 2, step = a.Length > 6 ? double.Parse(a[6], inv) : 0.5, shift = a.Length > 7 ? double.Parse(a[7], inv) : 0;
    string icons = a.Length > 8 ? a[8] : "death,kill";
    var (start, _) = SessionName(sessionDir);
    var (tlStart, _, root) = LoadTimeline(tlPath);
    double off = (start - tlStart).TotalSeconds;
    if (Environment.GetEnvironmentVariable("SPIKE_OFF") is { } offEnv) off = double.Parse(offEnv, inv);   // override: timeline time (s) at position 0 of the media (clips)
    chunk0Val = Environment.GetEnvironmentVariable("SPIKE_CHUNK0") is { } c0e ? double.Parse(c0e, inv) : 0;
    int lastChunk = Directory.GetFiles(sessionDir, "chunk-stream0-*.m4s").Select(f => int.Parse(Regex.Match(f, @"chunk-stream0-(\d+)\.m4s$").Groups[1].Value)).DefaultIfEmpty(0).Max();
    if (sessionDir.Replace('\\', '/').Contains("/clips/") && Environment.GetEnvironmentVariable("SPIKE_OFF") is null)
    {   // clip: precise bg start offset from clip.pb (field 1.5.10, ms) and absolute tfdt of chunk 1
        var clipDir = Path.GetFullPath(Path.Combine(sessionDir, "..", ".."));
        var pbs = new StringWriter(); var pbb = File.ReadAllBytes(Path.Combine(clipDir, "clip.pb")); Pb.Dump(pbb, 0, pbb.Length, "", pbs);
        off = double.Parse(Regex.Match(pbs.ToString(), @"^    10: (\d+)\s*$", RegexOptions.Multiline).Groups[1].Value, inv) / 1000.0;
        string c1 = Path.Combine(outDir, "cache2", name + "_c1.mp4"); Directory.CreateDirectory(Path.GetDirectoryName(c1)!);
        using (var o = File.Create(c1)) foreach (var f in new[] { "init-stream0.m4s", "chunk-stream0-00001.m4s" }) { using var s = new FileStream(Path.Combine(sessionDir, f), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); s.CopyTo(o); }
        chunk0Val = Math.Round(double.Parse(Run("ffprobe", $"-v error -select_streams v -show_entries packet=pts_time -read_intervals %+#1 -of csv=p=0 \"{c1}\"").Split('\n')[0].Trim().TrimEnd(','), inv) / 3.0) * 3.0;
        Console.WriteLine($"   clip: offset(clip.pb)={off:F3}s vs name-derived {(start - tlStart).TotalSeconds:F0}s, chunk1 tfdt={chunk0Val:F1}s, last chunk {lastChunk}");
    }
    var all = Events(root, icons.Split(',', StringSplitOptions.RemoveEmptyEntries));
    List<Marker> chosen;
    if (pick == "all")   // every marker whose +-span window lies inside the chunks on disk
        chosen = all.Where(m => m.TimeS - off - span - 0.5 >= chunk0Val && m.TimeS - off + span + 0.5 <= chunk0Val + 3.0 * lastChunk).ToList();
    else if (pick.StartsWith("auto:"))
    {
        int k = int.Parse(pick[5..], inv);
        var valid = all.Where(m => m.TimeS - off > span + 6).ToList();
        k = Math.Min(k, valid.Count);
        chosen = Enumerable.Range(0, k).Select(i => valid[(int)Math.Round(i * (valid.Count - 1) / (double)Math.Max(k - 1, 1))]).ToList();
    }
    else { var ids = pick.Split(','); chosen = all.Where(m => ids.Contains(m.Id)).ToList(); }
    string cache = Path.Combine(outDir, "cache2", name);
    Directory.CreateDirectory(cache);
    foreach (var m in chosen)
    {
        double pos = m.TimeS - off;
        string jpg = Path.Combine(outDir, "sheets2", name, $"m{m.Id}_{m.Icon}_{(shift >= 0 ? "p" : "m")}{Math.Abs(shift).ToString("F1", inv)}.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(jpg)!);
        MakeSheet(sessionDir, cache, pos + shift, span, step, jpg, pos);
        Console.WriteLine($"{m.Id,5} pos={pos,9:F3} {m.Icon,-14} {m.Title} -> {jpg.Replace('\\', '/')}");
    }
    return 0;
}

// Frames at (center-span .. center+span) in session time. Chunk N (1-based) is assumed to start at (N-1)*3 s; the real first-packet pts is printed.
void MakeSheet(string sessionDir, string cache, double center, double span, double step, string sheetPath, double markerPos)
{
    double t0 = Math.Max(center - span, 0);
    double chunk0 = chunk0Val;
    int first = (int)Math.Floor((t0 - chunk0) / 3.0) + 1, last = (int)Math.Floor((center + span - chunk0) / 3.0) + 1;
    string cat = Path.Combine(cache, $"v_{first}_{last}.mp4");
    if (!File.Exists(cat))
    {
        using var o = File.Create(cat);
        foreach (var n in new[] { "init-stream0.m4s" }.Concat(Enumerable.Range(first, last - first + 1).Select(n => $"chunk-stream0-{n:00000}.m4s")))
        {
            using var s = new FileStream(Path.Combine(sessionDir, n), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            s.CopyTo(o);
        }
    }
    double basePts = double.Parse(Run("ffprobe", $"-v error -select_streams v -show_entries packet=pts_time -read_intervals %+#1 -of csv=p=0 \"{cat}\"").Split('\n')[0].Trim().TrimEnd(','), inv);
    double rel = t0 - basePts;   // seek relative to the real first pts (accurate seek)
    Console.WriteLine($"   chunks {first}-{last} basePts={basePts.ToString("F3", inv)} expected={((first - 1) * 3.0).ToString("F3", inv)}");
    int n2 = (int)Math.Round(2 * span / step) + 1;
    string font = "C\\:/Windows/Fonts/arial.ttf";
    var vf = new StringBuilder();
    vf.Append($"fps=1/{step.ToString(inv)},scale=640:-2,");
    vf.Append($"drawtext=fontfile='{font}':text='%{{expr\\:round((n*{step.ToString(inv)}+{(t0 - markerPos).ToString(inv)})*100)/100}}':fontsize=30:fontcolor=yellow:box=1:boxcolor=black@0.7:x=8:y=8,");
    vf.Append($"tile=3x{(n2 + 2) / 3}");
    Run("ffmpeg", $"-v error -y -ss {rel.ToString("F3", inv)} -i \"{cat}\" -vf \"{vf}\" -frames:v 1 -q:v 3 \"{sheetPath}\"");
}

static string Run(string exe, string argline)
{
    var p = Process.Start(new ProcessStartInfo(exe, argline) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
    string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception($"{exe} failed: {e}");
    return o;
}

record Marker(string Id, double TimeS, string Icon, string Title, string Desc);

static class Pb
{
    // Minimal protobuf dump (wire types 0/1/2/5). Length-delimited fields are tried as nested messages, then printed as text.
    public static void Dump(byte[] b, int s, int e, string indent, TextWriter w)
    {
        int i = s;
        while (i < e)
        {
            ulong key = Varint(b, ref i); int field = (int)(key >> 3), wt = (int)(key & 7);
            if (wt == 0) w.WriteLine($"{indent}{field}: {Varint(b, ref i)}");
            else if (wt == 1) { w.WriteLine($"{indent}{field}: f64 {BitConverter.ToUInt64(b, i)}"); i += 8; }
            else if (wt == 5) { w.WriteLine($"{indent}{field}: f32 {BitConverter.ToUInt32(b, i)}"); i += 4; }
            else if (wt == 2)
            {
                int len = (int)Varint(b, ref i);
                if (len > 0 && TryNested(b, i, i + len)) { w.WriteLine($"{indent}{field}: {{"); Dump(b, i, i + len, indent + "  ", w); w.WriteLine($"{indent}}}"); }
                else w.WriteLine($"{indent}{field}: \"{System.Text.Encoding.UTF8.GetString(b, i, len)}\"");
                i += len;
            }
            else throw new InvalidDataException("wire type " + wt);
        }
    }
    static bool TryNested(byte[] b, int s, int e)
    {
        try
        {
            int i = s;
            while (i < e)
            {
                ulong key = Varint(b, ref i); int wt = (int)(key & 7); if ((key >> 3) == 0) return false;
                if (wt == 0) Varint(b, ref i); else if (wt == 1) i += 8; else if (wt == 5) i += 4;
                else if (wt == 2) { int l = (int)Varint(b, ref i); i += l; } else return false;
                if (i > e) return false;
            }
            return i == e;
        }
        catch { return false; }
    }
    static ulong Varint(byte[] b, ref int i) { ulong r = 0; int sh = 0; while (true) { byte x = b[i++]; r |= (ulong)(x & 0x7f) << sh; if ((x & 0x80) == 0) return r; sh += 7; } }
}

