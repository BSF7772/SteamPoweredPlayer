// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Net;

namespace SteamPoweredPlayer.Tests.Net;

public sealed class DownloadCoverageTests
{
    private static SessionMedia Media(params SegmentRun[] runs) => new("X", runs, 3.0, false, false);

    [Fact]
    public void Nothing_cached_is_neither_partial_nor_complete()
    {
        var c = DownloadCoverage.Of(Media(new SegmentRun(1, 10)), _ => false);
        Assert.Equal(0, c.Have);
        Assert.Equal(10, c.Total);
        Assert.False(c.IsPartial);
        Assert.False(c.IsComplete);
        Assert.Equal(0, c.Seconds);
    }

    [Fact]
    public void Some_chunks_are_partial_and_count_seconds_without_the_gaps()
    {
        var cached = new HashSet<int> { 3, 4, 5, 9 };
        var c = DownloadCoverage.Of(Media(new SegmentRun(1, 10)), cached.Contains);
        Assert.True(c.IsPartial);
        Assert.False(c.IsComplete);
        Assert.Equal(4, c.Have);
        Assert.Equal(12.0, c.Seconds);
        Assert.Equal(0.4, c.Fraction, 6);
    }

    [Fact]
    public void Every_chunk_cached_is_complete_even_across_runs()
    {
        var c = DownloadCoverage.Of(Media(new SegmentRun(1, 2), new SegmentRun(10, 11)), _ => true);
        Assert.True(c.IsComplete);
        Assert.False(c.IsPartial);
        Assert.Equal(1.0, c.Fraction);
    }

    [Fact]
    public void An_empty_recording_is_not_complete() =>
        Assert.False(DownloadCoverage.Of(Media(), _ => true).IsComplete);
}
