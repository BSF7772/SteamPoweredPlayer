// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Tests.Playback;

/// <summary>Decision D-RAM: the demuxer cache is seconds × bitrate (with headroom), clamped.</summary>
public sealed class DemuxerCacheTests
{
    private const long MiB = 1 << 20;

    [Fact]
    public void Typical_steam_bitrate_keeps_the_previous_64_plus_32_mib()
    {
        var p = DemuxerCache.For(22_000_000);
        Assert.Equal(20, p.ReadaheadSeconds);
        Assert.Equal(68_750_000, p.MaxBytes);                       // 2.75 MB/s × 1.25 × 20 s ≈ 65.6 MiB
        Assert.Equal(34_375_000, p.MaxBackBytes);                   // × 10 s ≈ 32.8 MiB
        Assert.InRange(p.TotalBytes, 96 * MiB, 100 * MiB);
        Assert.Equal(p.TotalBytes, DemuxerCache.ExpectedCacheBytes(22_000_000));
    }

    [Fact]
    public void Cache_scales_with_bitrate_between_floor_and_ceiling()
    {
        Assert.True(DemuxerCache.For(50_000_000).MaxBytes > DemuxerCache.For(22_000_000).MaxBytes);
        var low = DemuxerCache.For(4_000_000);
        Assert.Equal(DemuxerCache.MinForwardBytes, low.MaxBytes);
        Assert.Equal(DemuxerCache.MinBackBytes, low.MaxBackBytes);
        var high = DemuxerCache.For(900_000_000);
        Assert.Equal(DemuxerCache.MaxForwardBytes, high.MaxBytes);
        Assert.Equal(DemuxerCache.MaxBackBytesCeiling, high.MaxBackBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1e12)]
    public void Unknown_or_implausible_bitrate_uses_the_fallback(double? bps) =>
        Assert.Equal(DemuxerCache.For(DemuxerCache.FallbackBitsPerSecond), DemuxerCache.For(bps));

    [Fact]
    public void Media_overload_uses_the_measured_bitrate()
    {
        var m = new SessionMedia("C:/rec/x", [new SegmentRun(1, 10)], 3, false, true, BitsPerSecond: 30_000_000);
        Assert.Equal(DemuxerCache.For(30_000_000), DemuxerCache.For(m));
    }
}
