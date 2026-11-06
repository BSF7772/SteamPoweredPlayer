// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S2 driver. Subcommands:
//   edl   <session> <out.edl> [--first N] [--last M] [--exclude a-b] [--phantom N,N]
//   info  <session>
//   probe <folder> [--pid P]          share-mode probe (use on COPIES only)
//   run   <scenario> ...              mpv JSON-IPC measurements (see Scenarios.cs)
using System.Globalization;
using System.Text.Json;

namespace S2Tool;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: edl|info|probe|run ..."); return 2; }
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        switch (args[0])
        {
            case "edl": return Edl_(args);
            case "info": return Info(args);
            case "probe": return ShareProbe.Run(args);
            case "integrity": return Integrity.Run(args);
            case "probe-selftest": return ShareProbe.SelfTest(args);
            case "summary": return Summary.Run(args);
            case "run": return await Scenarios.Run(args);
            default: Console.Error.WriteLine("unknown command"); return 2;
        }
    }

    public static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static (int, int)[] ParseRanges(string? s) => s is null ? [] :
        s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => { var p = x.Split('-'); return (int.Parse(p[0]), int.Parse(p[^1])); }).ToArray();

    static int Edl_(string[] a)
    {
        var folder = a[1]; var outPath = a[2];
        int? first = Opt(a, "--first") is { } f ? int.Parse(f) : null;
        int? last = Opt(a, "--last") is { } l ? int.Parse(l) : null;
        var s = Scanner.Scan(folder, first, last, ParseRanges(Opt(a, "--exclude")), validateHeads: a.Contains("--validate"));
        var phantom = Opt(a, "--phantom")?.Split(',').Select(int.Parse).ToArray();
        var text = Edl.Build(s, phantom);
        File.WriteAllText(outPath, text, new System.Text.UTF8Encoding(false));
        Console.WriteLine(JsonSerializer.Serialize(new { runs = s.Runs, edlBytes = text.Length, lines = text.Count(c => c == '\n') }));
        return 0;
    }

    static int Info(string[] a)
    {
        var folder = a[1];
        var s = Scanner.Scan(folder);
        var tmp = Directory.EnumerateFiles(folder, "*.tmp").Select(Path.GetFileName).ToArray();
        var mpd = Path.Combine(folder, "session.mpd");
        long mpdLen = File.Exists(mpd) ? new FileInfo(mpd).Length : -1;
        bool mpdZero = mpdLen > 0 && File.ReadAllBytes(mpd).All(b => b == 0);
        var ts0 = Mp4.Timescale(Path.Combine(folder, "init-stream0.m4s"));
        var ts1 = Mp4.Timescale(Path.Combine(folder, "init-stream1.m4s"));
        object? Tfdt(int st, int n) { var p = Path.Combine(folder, $"chunk-stream{st}-{n:00000}.m4s"); return File.Exists(p) ? Mp4.BaseDecodeTime(p) : null; }
        var fr = s.Runs.Count > 0 ? s.Runs[0].First : 0;
        var lr = s.Runs.Count > 0 ? s.Runs[^1].First + s.Runs[^1].Count - 1 : 0;
        var o = new
        {
            runs = s.Runs, tmp, mpdLen, mpdZero, timescaleVideo = ts0, timescaleAudio = ts1,
            firstSegment = fr, lastSegment = lr,
            tfdtFirst = new { video = Tfdt(0, fr), audio = Tfdt(1, fr) },
            tfdtLast = new { video = Tfdt(0, lr), audio = Tfdt(1, lr) },
            tfdtFirstSeconds = new { video = Tfdt(0, fr) is ulong v && ts0 is uint t0 ? (double)v / t0 : (double?)null, audio = Tfdt(1, fr) is ulong w && ts1 is uint t1 ? (double)w / t1 : (double?)null },
        };
        Console.WriteLine(JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}

public static class Integrity
{
    /// <summary>Chunks whose first 8 bytes are all zero (Steam preallocates; crash leaves zero-filled files) or that have no moof within 4 KB.</summary>
    public static int Run(string[] a)
    {
        var folder = a[1];
        var bad = new List<string>(); int total = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "chunk-stream*.m4s").OrderBy(x => x))
        {
            total++;
            var len = new FileInfo(f).Length;
            var h = Mp4.ReadHead(f, 4096);
            bool zero = h.Take(8).All(b => b == 0);
            bool noMoof = !zero && Mp4.Find(h, 0, h.Length, "moof") is null;
            if (zero || noMoof || len < 1024) bad.Add($"{Path.GetFileName(f)} size={len} {(zero ? "ZERO" : noMoof ? "NO_MOOF" : "SHORT")}");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { total, bad = bad.Count, first = bad.Take(15), last = bad.TakeLast(5) }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
