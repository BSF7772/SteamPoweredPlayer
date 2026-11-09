// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace SteamPoweredPlayer.Core.Export;

/// <summary>One MP4 edit: <paramref name="Duration"/> on the movie's timescale; <paramref name="MediaTime"/> on the track's, −1 for a stretch with nothing.</summary>
public readonly record struct EditEntry(long Duration, long MediaTime);

/// <summary>A file's movie timescale and those of its picture and sound tracks.</summary>
public readonly record struct Mp4Timescales(uint Movie, uint Video, uint Audio);

/// <summary>
/// Replaces the edit lists of an MP4's picture and sound tracks, nothing else: no sample moves, so a lossless export stays bit for bit
/// what Steam wrote. The durations in <c>mvhd</c> and <c>tkhd</c> follow, and chunk offsets move with the <c>moov</c> box when its size changes.
/// </summary>
public static class Mp4EditList
{
    private readonly record struct Box(string Type, int Start, int Header, int Size)
    {
        public int Body => Start + Header;
        public int End => Start + Size;
    }

    public static Mp4Timescales ReadTimescales(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadTimescales(ReadMoov(file, out _));
    }

    /// <summary>The timescales held in a whole <c>moov</c> box.</summary>
    public static Mp4Timescales ReadTimescales(byte[] moov)
    {
        var root = Children(moov, 0, moov.Length).Single();
        var mvhd = Child(moov, root, "mvhd") ?? throw Bad("no mvhd");
        var movie = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Body + (moov[mvhd.Body] == 1 ? 20 : 12)));
        uint video = 0, audio = 0;
        foreach (var trak in Children(moov, root.Body, root.End).Where(b => b.Type == "trak"))
        {
            var mdia = Child(moov, trak, "mdia") ?? throw Bad("no mdia");
            var mdhd = Child(moov, mdia, "mdhd") ?? throw Bad("no mdhd");
            var scale = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mdhd.Body + (moov[mdhd.Body] == 1 ? 20 : 12)));
            if (Handler(moov, mdia) == "vide") video = scale;
            else if (Handler(moov, mdia) == "soun") audio = scale;
        }
        return new Mp4Timescales(movie, video, audio);
    }

    /// <summary>Writes <paramref name="output"/>: <paramref name="input"/> with the given edit lists.</summary>
    public static void Rewrite(string input, string output, IReadOnlyList<EditEntry> video, IReadOnlyList<EditEntry> audio)
    {
        using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        var moov = ReadMoov(source, out var at);
        var rebuilt = RewriteMoov(moov, at, video, audio);
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.Position = 0;
        CopyBytes(source, target, at);
        target.Write(rebuilt);
        source.Position = at + moov.Length;
        source.CopyTo(target);
    }

    /// <summary>
    /// The new <c>moov</c> for one read from offset <paramref name="moovAt"/> of its file. Chunk offsets past the old <c>moov</c> move by
    /// the size difference, because the new box takes its place.
    /// </summary>
    public static byte[] RewriteMoov(byte[] moov, long moovAt, IReadOnlyList<EditEntry> video, IReadOnlyList<EditEntry> audio)
    {
        var root = Children(moov, 0, moov.Length).Single();
        if (root.Type != "moov") throw Bad("not a moov box");
        var movieDuration = Math.Max(video.Sum(e => e.Duration), audio.Sum(e => e.Duration));
        using var body = new MemoryStream(moov.Length + 256);
        foreach (var child in Children(moov, root.Body, root.End))
        {
            var bytes = moov.AsSpan(child.Start, child.Size).ToArray();
            if (child.Type == "mvhd") SetDuration(bytes, child.Header, bytes[child.Header] == 1 ? 24 : 16, movieDuration);
            else if (child.Type == "trak")
            {
                var mdia = Child(moov, child, "mdia") ?? throw Bad("no mdia");
                var entries = Handler(moov, mdia) switch { "vide" => video, "soun" => audio, _ => null };
                if (entries is not null) bytes = RewriteTrak(moov, child, entries);
            }
            body.Write(bytes);
        }
        var result = new byte[8 + body.Length];
        WriteHeader(result, "moov", result.Length);
        body.Position = 0;
        body.ReadExactly(result, 8, (int)body.Length);
        ShiftChunkOffsets(result, 0, result.Length, moovAt, result.Length - moov.Length);
        return result;
    }

    private static byte[] RewriteTrak(byte[] moov, Box trak, IReadOnlyList<EditEntry> entries)
    {
        using var body = new MemoryStream(trak.Size + 256);
        foreach (var child in Children(moov, trak.Body, trak.End))
        {
            if (child.Type == "edts") continue;
            var bytes = moov.AsSpan(child.Start, child.Size).ToArray();
            if (child.Type == "tkhd")
            {
                SetDuration(bytes, child.Header, bytes[child.Header] == 1 ? 28 : 20, entries.Sum(e => e.Duration));
                body.Write(bytes);
                body.Write(Edts(entries));
                continue;
            }
            body.Write(bytes);
        }
        var result = new byte[8 + body.Length];
        WriteHeader(result, "trak", result.Length);
        body.Position = 0;
        body.ReadExactly(result, 8, (int)body.Length);
        return result;
    }

    private static byte[] Edts(IReadOnlyList<EditEntry> entries)
    {
        var wide = entries.Any(e => e.Duration > uint.MaxValue || e.MediaTime > int.MaxValue);
        var elstSize = 8 + 8 + entries.Count * (wide ? 20 : 12);
        var result = new byte[8 + elstSize];
        WriteHeader(result, "edts", result.Length);
        var box = result.AsSpan(8);
        WriteHeader(box, "elst", elstSize);
        box[8] = wide ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(box[12..], (uint)entries.Count);
        var p = 16;
        foreach (var e in entries)
        {
            if (wide)
            {
                BinaryPrimitives.WriteInt64BigEndian(box[p..], e.Duration);
                BinaryPrimitives.WriteInt64BigEndian(box[(p + 8)..], e.MediaTime);
                p += 16;
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(box[p..], (uint)e.Duration);
                BinaryPrimitives.WriteInt32BigEndian(box[(p + 4)..], (int)e.MediaTime);
                p += 8;
            }
            BinaryPrimitives.WriteInt32BigEndian(box[p..], 0x00010000);                      // rate 1.0
            p += 4;
        }
        return result;
    }

    private static void SetDuration(byte[] box, int header, int offset, long value)
    {
        if (box[header] == 1) BinaryPrimitives.WriteInt64BigEndian(box.AsSpan(header + offset), value);
        else BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(header + offset), checked((uint)value));
    }

    private static void ShiftChunkOffsets(byte[] d, int start, int end, long moovAt, long delta)
    {
        foreach (var b in Children(d, start, end))
        {
            switch (b.Type)
            {
                case "moov" or "trak" or "mdia" or "minf" or "stbl":
                    ShiftChunkOffsets(d, b.Body, b.End, moovAt, delta);
                    break;
                case "stco":
                    for (var (n, p) = (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b.Body + 4)), b.Body + 8); n > 0; n--, p += 4)
                    {
                        var v = (long)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
                        if (v > moovAt) BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(p), checked((uint)(v + delta)));
                    }
                    break;
                case "co64":
                    for (var (n, p) = (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b.Body + 4)), b.Body + 8); n > 0; n--, p += 8)
                    {
                        var v = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(p));
                        if (v > moovAt) BinaryPrimitives.WriteInt64BigEndian(d.AsSpan(p), v + delta);
                    }
                    break;
            }
        }
    }

    private static string? Handler(byte[] d, Box mdia) =>
        Child(d, mdia, "hdlr") is { } hdlr ? Encoding.ASCII.GetString(d, hdlr.Body + 8, 4) : null;

    private static Box? Child(byte[] d, Box parent, string type)
    {
        foreach (var b in Children(d, parent.Body, parent.End))
            if (b.Type == type) return b;
        return null;
    }

    private static List<Box> Children(byte[] d, int start, int end)
    {
        var result = new List<Box>();
        var i = start;
        while (i + 8 <= end)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i));
            var header = 8;
            if (size == 1) { size = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(i + 8)); header = 16; }
            else if (size == 0) size = end - i;
            if (size < header || i + size > end) throw Bad("damaged box");
            result.Add(new Box(Encoding.ASCII.GetString(d, i + 4, 4), i, header, (int)size));
            i += (int)size;
        }
        return result;
    }

    private static byte[] ReadMoov(FileStream file, out long at)
    {
        Span<byte> head = stackalloc byte[16];
        long position = 0;
        while (position + 8 <= file.Length)
        {
            file.Position = position;
            file.ReadExactly(head[..8]);
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(head);
            if (size == 1) { file.ReadExactly(head[8..16]); size = BinaryPrimitives.ReadInt64BigEndian(head[8..]); }
            else if (size == 0) size = file.Length - position;
            if (size < 8) throw Bad("damaged top-level box");
            if (head[4] == 'm' && head[5] == 'o' && head[6] == 'o' && head[7] == 'v')
            {
                var moov = new byte[size];
                file.Position = position;
                file.ReadExactly(moov);
                at = position;
                return moov;
            }
            position += size;
        }
        throw Bad("no moov box");
    }

    private static void CopyBytes(Stream from, Stream to, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            var n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            to.Write(buffer, 0, n);
            count -= n;
        }
    }

    private static void WriteHeader(Span<byte> target, string type, int size)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target, (uint)size);
        Encoding.ASCII.GetBytes(type, target[4..]);
    }

    private static InvalidDataException Bad(string what) => new("Unexpected MP4 layout: " + what + ".");
}

/// <summary>A kept range on the source axis. An open end also keeps the frames a few ms past it, at the recording's own first or last chunk.</summary>
public readonly record struct LosslessRange(double Start, double End, bool OpenHead = false, bool OpenTail = false);

/// <summary>A stored track as two parallel lists in file order: each packet's pts in seconds on the source axis, and in ticks on the file's media axis.</summary>
public sealed record StoredPackets(IReadOnlyList<double> Source, IReadOnlyList<long> Ticks);

/// <summary>
/// Pure: the edit lists that make a file show exactly the source frames in each kept range, in order, with the sound that belongs to
/// them. The stored picture holds whole chunks because a chunk's first frame is the only keyframe; everything else is hidden by these edits.
/// </summary>
public static class LosslessEdits
{
    private const double StartTolerance = 0.001;                 // a frame stamped exactly on A is kept
    private const double FrameLengthFallback = 1.0 / 60;
    private const double GapSeconds = 0.25;                      // a next frame this far off belongs to another stored chunk

    /// <param name="audioDelay">Seconds to delay the sound against the picture (negative = earlier).</param>
    /// <returns>The picture's and the sound's edits; empty lists when no frame falls in any range.</returns>
    public static (List<EditEntry> Video, List<EditEntry> Audio) Compute(IReadOnlyList<LosslessRange> ranges, StoredPackets video, StoredPackets audio,
        Mp4Timescales scales, double audioDelay)
    {
        if (video.Source.Count != video.Ticks.Count || audio.Source.Count != audio.Ticks.Count)
            throw new InvalidOperationException("The joined file holds a different number of packets than the source.");
        var shown = Enumerable.Range(0, video.Source.Count).OrderBy(i => video.Source[i]).ToArray();     // presentation order
        var times = shown.Select(i => video.Source[i]).ToArray();
        var picture = new List<EditEntry>(ranges.Count);
        var sound = new List<EditEntry>(ranges.Count);
        var audioTimes = audio.Source.ToArray();
        for (var r = 0; r < ranges.Count; r++)
        {
            var range = ranges[r];
            var first = range.OpenHead ? 0 : LowerBound(times, range.Start - StartTolerance);
            var next = range.OpenTail ? times.Length : LowerBound(times, range.End);
            if (next <= first) continue;
            var startTick = video.Ticks[shown[first]];
            var lastTick = video.Ticks[shown[next - 1]];
            var endTick = next < times.Length && times[next] - times[next - 1] < GapSeconds
                ? video.Ticks[shown[next]]
                : lastTick + (long)Math.Round(FrameLength(times, next - 1) * scales.Video);
            // One movie tick short, so rounding can't let the next frame in.
            var duration = Math.Max(1, (endTick - startTick) * scales.Movie / scales.Video - 1);
            picture.Add(new EditEntry(duration, startTick));
            // The sound of the last range runs to B even if the picture stops short of it, as it does on a live recording's newest chunks.
            var last = r == ranges.Count - 1;
            var soundLength = last && !range.OpenTail ? Math.Max(duration, (long)((range.End - times[first]) * scales.Movie)) : duration;
            AddSound(sound, audioTimes, audio.Ticks, times[first] - audioDelay, soundLength, scales, last);
        }
        return (picture, sound);
    }

    private static void AddSound(List<EditEntry> sound, double[] times, IReadOnlyList<long> ticks, double from, long duration, Mp4Timescales scales, bool last)
    {
        var remaining = duration;
        if (times.Length > 0)
        {
            if (from < times[0])                                                                  // the recording's sound starts later: silence until then
            {
                var lead = Math.Min(remaining, (long)Math.Round((times[0] - from) * scales.Movie));
                if (lead > 0) { sound.Add(new EditEntry(lead, -1)); remaining -= lead; }
                from = times[0];
            }
            var length = times.Length > 1 ? times[^1] - times[^2] : 1024.0 / Math.Max(1, scales.Audio);
            var end = times[^1] + length;
            if (remaining > 0 && from < end)
            {
                var j = Math.Max(0, UpperBound(times, from) - 1);
                var play = Math.Min(remaining, (long)Math.Floor((end - from) * scales.Movie));
                if (play > 0)
                {
                    sound.Add(new EditEntry(play, ticks[j] + (long)Math.Round((from - times[j]) * scales.Audio)));
                    remaining -= play;
                }
            }
        }
        if (remaining > 0 && !last) sound.Add(new EditEntry(remaining, -1));                       // keeps the next range's sound on its picture
    }

    private static double FrameLength(double[] times, int i)
    {
        var d = i > 0 ? times[i] - times[i - 1] : FrameLengthFallback;
        return d is > 0 and < GapSeconds ? d : FrameLengthFallback;
    }

    private static int LowerBound(double[] a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { var m = (lo + hi) >>> 1; if (a[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    private static int UpperBound(double[] a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { var m = (lo + hi) >>> 1; if (a[m] <= v) lo = m + 1; else hi = m; }
        return lo;
    }
}
