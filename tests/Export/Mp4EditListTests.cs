// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using SteamPoweredPlayer.Core.Export;

namespace SteamPoweredPlayer.Tests.Export;

/// <summary>Edit lists are what make Save clip show exactly [A,B): the numbers and the boxes, without a video file.</summary>
public sealed class Mp4EditListTests
{
    private static readonly Mp4Timescales Scales = new(6_000_000, 1_000_000, 48_000);

    // P B B order: packets in file order, pts in steps of 10 ms, ticks = source − 100 s.
    private static StoredPackets Video(params double[] source) =>
        new(source, source.Select(s => (long)Math.Round((s - 100) * 1_000_000)).ToList());

    private static StoredPackets Audio(double from, int count)
    {
        var source = Enumerable.Range(0, count).Select(i => from + i * 1024.0 / 48000).ToList();
        return new(source, source.Select(s => (long)Math.Round((s - from) * 48000)).ToList());
    }

    [Fact]
    public void One_range_shows_the_frames_from_a_up_to_b_and_stops_before_the_next_frame()
    {
        var video = Video(100.00, 100.03, 100.01, 100.02, 100.06, 100.04, 100.05);       // shown order 100.00 .. 100.06
        var (v, a) = LosslessEdits.Compute([new(100.015, 100.045)], video, Audio(100, 50), Scales, 0);
        var e = Assert.Single(v);
        Assert.Equal(20_000, e.MediaTime);                                                 // 100.02, the first frame past A
        Assert.Equal((100.05 - 100.02) * 6_000_000 - 1, e.Duration, 1.0);                  // up to 100.05, which isn't shown (B is before it)
        Assert.Equal(e.Duration, Assert.Single(a).Duration);
    }

    [Fact]
    public void A_frame_stored_after_b_is_hidden_though_it_decodes_before_frames_shown()
    {
        // The real case: P at +2.6 ms past B is stored before the B-frames shown ahead of it.
        var video = Video(100.00, 100.03, 100.01, 100.02);
        var (v, _) = LosslessEdits.Compute([new(100.00, 100.025)], video, Audio(100, 50), Scales, 0);
        var e = Assert.Single(v);
        Assert.True(e.MediaTime + e.Duration * 1_000_000 / 6_000_000 < 30_000, "the frame at 100.03 would show");
    }

    [Fact]
    public void Every_range_gets_its_own_entry_and_the_sound_follows_each()
    {
        var video = Video(Enumerable.Range(0, 300).Select(i => 100 + i * 0.01).ToArray());
        var (v, a) = LosslessEdits.Compute([new(100.5, 100.8), new(101.0, 101.1), new(102.0, 102.5)], video, Audio(100, 400), Scales, 0);
        Assert.Equal(3, v.Count);
        Assert.Equal(3, a.Count);
        Assert.Equal([500_000L, 1_000_000L, 2_000_000L], v.Select(e => e.MediaTime));
        foreach (var (picture, sound) in v.Zip(a)) Assert.InRange(sound.Duration - picture.Duration, 0, 1);   // the last range's sound runs to B
        Assert.Equal([24_000L, 48_000L, 96_000L], a.Select(e => e.MediaTime));
    }

    [Theory]
    [InlineData(0.3, 100.2)]               // sound 0.3 s later: at the picture's 100.5 plays the source's 100.2
    [InlineData(-0.5, 101.0)]              // 0.5 s earlier: the source's 101.0
    public void A_sync_shift_moves_the_sounds_start(double delay, double expectedSource)
    {
        var video = Video(Enumerable.Range(0, 300).Select(i => 100 + i * 0.01).ToArray());
        var (_, a) = LosslessEdits.Compute([new(100.5, 101)], video, Audio(100, 400), Scales, delay);
        Assert.Equal((long)Math.Round((expectedSource - 100) * 48000), Assert.Single(a).MediaTime);
    }

    [Fact]
    public void Sound_missing_at_the_recordings_head_is_a_stretch_of_nothing_so_it_stays_in_sync()
    {
        var video = Video(Enumerable.Range(0, 100).Select(i => 100 + i * 0.01).ToArray());
        var (v, a) = LosslessEdits.Compute([new(100, 100.9)], video, Audio(100, 100), Scales, 0.5);
        Assert.Equal(2, a.Count);
        Assert.Equal(-1, a[0].MediaTime);
        Assert.Equal(3_000_000, a[0].Duration);
        Assert.Equal(0, a[1].MediaTime);
        Assert.InRange(a.Sum(e => e.Duration) - v[0].Duration, 0, 1);
    }

    [Fact]
    public void Sound_missing_between_ranges_keeps_the_next_range_on_its_picture()
    {
        var video = Video(Enumerable.Range(0, 300).Select(i => 100 + i * 0.01).ToArray());
        var (v, a) = LosslessEdits.Compute([new(100, 100.5), new(102, 102.5)], video, Audio(100, 12), Scales, 0);     // sound for 0.256 s only
        Assert.Equal(-1, a[1].MediaTime);
        Assert.Equal(v[0].Duration, a[0].Duration + a[1].Duration);
    }

    [Fact]
    public void An_open_tail_keeps_a_last_frame_a_few_ms_past_b()
    {
        var video = Video(100.00, 100.01, 100.02);
        var (open, _) = LosslessEdits.Compute([new(100, 100.015, OpenTail: true)], video, Audio(100, 50), Scales, 0);
        var (strict, _) = LosslessEdits.Compute([new(100, 100.015)], video, Audio(100, 50), Scales, 0);
        Assert.True(open[0].Duration > strict[0].Duration + 10_000);
    }

    [Fact]
    public void A_different_packet_count_is_refused_rather_than_guessed()
    {
        var bad = new StoredPackets([1.0, 2.0], [1L]);
        Assert.Throws<InvalidOperationException>(() => LosslessEdits.Compute([new(0, 3)], bad, Audio(0, 5), Scales, 0));
    }

    [Fact]
    public void The_moov_gets_new_edits_and_durations_and_chunk_offsets_follow_its_size()
    {
        const long moovAt = 36;
        var moov = Moov(oldEntries: 1);
        Assert.Equal(new Mp4Timescales(6_000_000, 1_000_000, 48_000), Mp4EditList.ReadTimescales(moov));

        EditEntry[] video = [new(6_000_000, 500), new(3_000_000, 2_000_000), new(1_500_000, 4_000_000)];
        EditEntry[] audio = [new(10_500_000, 24)];
        var rebuilt = Mp4EditList.RewriteMoov(moov, moovAt, video, audio);
        var delta = rebuilt.Length - moov.Length;
        Assert.Equal(2 * 12, delta);                                                        // two more entries on the picture

        Assert.Equal(10_500_000u, BinaryPrimitives.ReadUInt32BigEndian(Find(rebuilt, "mvhd").AsSpan(8 + 16)));
        var entries = Elst(rebuilt, 0);
        Assert.Equal(video, entries);
        Assert.Equal(audio, Elst(rebuilt, 1));
        Assert.Equal(10_500_000u, BinaryPrimitives.ReadUInt32BigEndian(Find(rebuilt, "tkhd").AsSpan(8 + 20)));

        var stco = Find(rebuilt, "stco");
        Assert.Equal(moovAt + 1000 + delta, BinaryPrimitives.ReadUInt32BigEndian(stco.AsSpan(8 + 8)));     // past the moov: moved
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32BigEndian(stco.AsSpan(8 + 12)));                       // before it: stays
    }

    [Fact]
    public void Times_too_large_for_32_bits_use_a_version_1_list()
    {
        var rebuilt = Mp4EditList.RewriteMoov(Moov(oldEntries: 1), 36, [new(100, 3_000_000_000)], [new(100, 1)]);
        var elst = Find(rebuilt, "elst");
        Assert.Equal(1, elst[8]);
        Assert.Equal(100, BinaryPrimitives.ReadInt64BigEndian(elst.AsSpan(8 + 8)));
        Assert.Equal(3_000_000_000, BinaryPrimitives.ReadInt64BigEndian(elst.AsSpan(8 + 16)));
    }

    [Fact]
    public void A_whole_file_rewrite_keeps_every_other_byte()
    {
        var dir = Directory.CreateTempSubdirectory("spp-elst-");
        try
        {
            var ftyp = Box("ftyp", new byte[20]);
            var moov = Moov(oldEntries: 1);
            var mdat = Box("mdat", Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
            var input = Path.Combine(dir.FullName, "in.mp4");
            var output = Path.Combine(dir.FullName, "out.mp4");
            File.WriteAllBytes(input, [.. ftyp, .. moov, .. mdat]);
            Mp4EditList.Rewrite(input, output, [new(100, 5), new(200, 50)], [new(300, 1)]);
            var result = File.ReadAllBytes(output);
            Assert.Equal(ftyp, result[..ftyp.Length]);
            Assert.Equal(mdat, result[^mdat.Length..]);
            Assert.Equal(new Mp4Timescales(6_000_000, 1_000_000, 48_000), Mp4EditList.ReadTimescales(output));
        }
        finally { dir.Delete(true); }
    }

    // ---- a tiny hand-made moov ----------------------------------------------------------------------------------------

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var result = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes(type, result.AsSpan(4));
        body.CopyTo(result, 8);
        return result;
    }

    private static byte[] U32(params uint[] values)
    {
        var result = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), values[i]);
        return result;
    }

    private static byte[] Track(string handler, uint scale, int oldEntries) =>
        Box("trak",
            Box("tkhd", U32(0, 0, 0, 1, 0, 99, 0, 0, 0, 0, 0)),
            Box("edts", Box("elst", U32(0, (uint)oldEntries), U32(Enumerable.Range(0, oldEntries).SelectMany(_ => new uint[] { 5, 6, 0x10000 }).ToArray()))),
            Box("mdia",
                Box("mdhd", U32(0, 0, 0, scale, 99, 0)),
                Box("hdlr", U32(0, 0), Encoding.ASCII.GetBytes(handler), new byte[12]),
                Box("minf", Box("stbl", Box("stco", U32(0, 2, 36 + 1000, 10))))));

    private static byte[] Moov(int oldEntries) =>
        Box("moov",
            Box("mvhd", U32(0, 0, 0, 6_000_000, 99, 0)),
            Track("vide", 1_000_000, oldEntries),
            Track("soun", 48_000, oldEntries));

    private static byte[] Find(byte[] data, string type) => FindAll(data, type)[0];

    private static List<byte[]> FindAll(byte[] data, string type)
    {
        var name = Encoding.ASCII.GetBytes(type);
        var result = new List<byte[]>();
        for (var i = 4; i + 4 <= data.Length; i++)
            if (data.AsSpan(i, 4).SequenceEqual(name))
            {
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i - 4));
                result.Add(data[(i - 4)..(i - 4 + size)]);
            }
        return result;
    }

    private static EditEntry[] Elst(byte[] moov, int track)
    {
        var elst = FindAll(moov, "elst")[track];
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(elst.AsSpan(12));
        return Enumerable.Range(0, count).Select(i => new EditEntry(
            BinaryPrimitives.ReadUInt32BigEndian(elst.AsSpan(16 + i * 12)),
            BinaryPrimitives.ReadInt32BigEndian(elst.AsSpan(20 + i * 12)))).ToArray();
    }
}
