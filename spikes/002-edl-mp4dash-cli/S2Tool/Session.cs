// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S2: session scanner (complete chunk numbers -> runs), EDL builder, tiny fMP4 box reader.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace S2Tool;

public sealed record Run(int First, int Count);

public sealed record SessionMedia(string Folder, IReadOnlyList<Run> Runs, double SegmentSeconds);

public static partial class Scanner
{
    [GeneratedRegex(@"^chunk-stream([01])-(\d{5})\.m4s$")]
    private static partial Regex ChunkRegex();

    /// <summary>One directory listing. Segment N is complete when both tracks have it and neither is a .tmp.</summary>
    public static SessionMedia Scan(string folder, int? first = null, int? last = null, IEnumerable<(int a, int b)>? exclude = null, bool validateHeads = false)
    {
        var have = new HashSet<int>[2] { new(), new() };
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var m = ChunkRegex().Match(Path.GetFileName(path));
            if (m.Success) have[m.Groups[1].Value == "0" ? 0 : 1].Add(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        if (validateHeads)
            for (int st = 0; st < 2; st++)
                have[st].RemoveWhere(n => Mp4.IsZeroHeaded(Path.Combine(folder, $"chunk-stream{st}-{n:00000}.m4s")));
        var complete = have[0].Intersect(have[1]).OrderBy(n => n)
            .Where(n => (first is null || n >= first) && (last is null || n <= last))
            .Where(n => exclude is null || !exclude.Any(e => n >= e.a && n <= e.b))
            .ToList();
        var runs = new List<Run>();
        foreach (var n in complete)
        {
            if (runs.Count > 0 && runs[^1].First + runs[^1].Count == n) runs[^1] = runs[^1] with { Count = runs[^1].Count + 1 };
            else runs.Add(new Run(n, 1));
        }
        return new SessionMedia(folder, runs, 3.0);
    }
}

public static class Edl
{
    /// <summary>Same shape as the plan's Phase 1 task 1.6 sketch. <paramref name="phantom"/> adds chunk numbers that are NOT on disk (vanished-file test).</summary>
    public static string Build(SessionMedia s, IEnumerable<int>? phantom = null, string? lengthOverride = null, bool lead = false)
    {
        var sb = new StringBuilder("# mpv EDL v0\n!no_chapters\n");
        Track(sb, s, 0, phantom, lengthOverride, lead);
        sb.Append("!new_stream\n");
        Track(sb, s, 1, phantom, lengthOverride, lead);
        return sb.ToString();
    }

    static void Track(StringBuilder sb, SessionMedia s, int st, IEnumerable<int>? phantom, string? lengthOverride, bool lead)
    {
        sb.Append("!mp4_dash,init=").Append(Esc(Path.Combine(s.Folder, $"init-stream{st}.m4s").Replace((char)92, '/'))).Append((char)10);
        var len = lengthOverride ?? s.SegmentSeconds.ToString(CultureInfo.InvariantCulture);
        var nums = s.Runs.SelectMany(r => Enumerable.Range(r.First, r.Count)).Concat(phantom ?? []).OrderBy(n => n).ToList();
        for (int i = 0; i < nums.Count; i++)
        {
            int n = nums[i];
            var l = len;
            if (lead)
            {
                // Lead mode: EDL axis == media (tfdt) axis. Chunk n starts at (n-1)*3 s, so part i covers [3(n-1), 3(next-1)):
                // the FIRST part also covers everything before it, and a part before a gap is stretched over the hole.
                int next = i + 1 < nums.Count ? nums[i + 1] : n + 1;
                double secs = s.SegmentSeconds * (next - n) + (i == 0 ? s.SegmentSeconds * (n - 1) : 0);
                l = secs.ToString(CultureInfo.InvariantCulture);
            }
            sb.Append(Esc(Path.Combine(s.Folder, $"chunk-stream{st}-{n:00000}.m4s").Replace((char)92, '/')))
              .Append(",length=").Append(l).Append((char)10);
        }
    }

    // %<utf8 byte count>%value
    public static string Esc(string v) => $"%{Encoding.UTF8.GetByteCount(v)}%{v}";
}

/// <summary>Minimal ISO-BMFF reader: init timescale (mdhd) and chunk base decode time (moof/traf/tfdt).</summary>
public static class Mp4
{
    static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "moof", "traf"];

    public static bool IsZeroHeaded(string path)
    {
        try { var h = ReadHead(path, 8); return h.Length < 8 || h.All(b => b == 0); } catch (IOException) { return true; }
    }

    public static byte[] ReadHead(string path, int max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[(int)Math.Min(max, fs.Length)];
        fs.ReadExactly(buf);
        return buf;
    }

    public static IEnumerable<(string type, int payloadStart, int payloadEnd)> Boxes(byte[] b, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            long size = (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
            string type = Encoding.ASCII.GetString(b, p + 4, 4);
            int hdr = 8;
            if (size == 1) { size = (long)ReadU64(b, p + 8); hdr = 16; }
            if (size == 0) size = end - p;
            if (size < hdr) yield break;
            int pe = (int)Math.Min(p + size, end);
            yield return (type, p + hdr, pe);
            p = (int)(p + size);
        }
    }

    public static (string type, int s, int e)? Find(byte[] b, int start, int end, params string[] path)
    {
        foreach (var (t, s, e) in Boxes(b, start, end))
        {
            if (t != path[0]) continue;
            if (path.Length == 1) return (t, s, e);
            var r = Find(b, s, e, path[1..]);
            if (r is not null) return r;
        }
        return null;
    }

    static uint ReadU32(byte[] b, int p) => (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
    static ulong ReadU64(byte[] b, int p) => (ulong)ReadU32(b, p) << 32 | ReadU32(b, p + 4);

    public static uint? Timescale(string initPath)
    {
        var b = ReadHead(initPath, 4096);
        var m = Find(b, 0, b.Length, "moov", "trak", "mdia", "mdhd");
        if (m is null) return null;
        int p = m.Value.s;
        int ver = b[p];
        return ReadU32(b, p + 4 + (ver == 1 ? 16 : 8));
    }

    public static ulong? BaseDecodeTime(string chunkPath)
    {
        var b = ReadHead(chunkPath, 4096);
        var m = Find(b, 0, b.Length, "moof", "traf", "tfdt");
        if (m is null) return null;
        int p = m.Value.s;
        return b[p] == 1 ? ReadU64(b, p + 4) : ReadU32(b, p + 4);
    }
}
