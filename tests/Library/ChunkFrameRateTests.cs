// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class ChunkFrameRateTests
{
    private static byte[] Box(string type, params byte[][] children)
    {
        var size = 8 + children.Sum(c => c.Length);
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)size);
        Encoding.ASCII.GetBytes(type, b.AsSpan(4));
        var at = 8;
        foreach (var c in children) { c.CopyTo(b, at); at += c.Length; }
        return b;
    }

    private static byte[] Trun(uint samples)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), samples);
        return Box("trun", payload);
    }

    private static byte[] Fragment(uint samples) =>
        [.. Box("moof", Box("mfhd", new byte[8]), Box("traf", Box("tfhd", new byte[8]), Trun(samples))), .. Box("mdat", new byte[32])];

    [Fact]
    public void Counts_the_samples_of_every_fragment()
    {
        byte[] chunk = [.. Box("styp", new byte[8]), .. Fragment(60), .. Fragment(60), .. Fragment(58)];
        Assert.Equal(178, ChunkFrameRate.CountFrames(new MemoryStream(chunk)));
    }

    [Fact]
    public void A_chunk_cut_inside_a_moof_counts_the_fragments_before_it()
    {
        byte[] chunk = [.. Fragment(60), .. Fragment(60)];
        Assert.Equal(60, ChunkFrameRate.CountFrames(new MemoryStream(chunk[..^50])));     // the last 40 bytes are its mdat
        Assert.Equal(0, ChunkFrameRate.CountFrames(new MemoryStream(new byte[] { 0, 0, 0 })));
    }

    [Fact]
    public void Measure_uses_the_fullest_chunk_and_survives_a_removed_one()
    {
        var dir = Directory.CreateTempSubdirectory("spp-fps-");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "chunk-stream0-00002.m4s"), Fragment(178));
            File.WriteAllBytes(Path.Combine(dir.FullName, "chunk-stream0-00003.m4s"), Fragment(180));
            var media = new SessionMedia(dir.FullName, [new SegmentRun(1, 3)], 3, false, false);
            Assert.Equal(60, ChunkFrameRate.Measure(media));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Measure_gives_null_when_nothing_is_readable()
    {
        var media = new SessionMedia(Path.Combine(Path.GetTempPath(), "spp-missing-" + Guid.NewGuid()), [new SegmentRun(1, 3)], 3, false, false);
        Assert.Null(ChunkFrameRate.Measure(media));
    }
}
