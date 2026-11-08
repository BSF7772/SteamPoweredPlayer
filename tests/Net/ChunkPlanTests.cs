// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Net;

namespace SteamPoweredPlayer.Tests.Net;

public sealed class ChunkPlanTests
{
    private static SessionMedia Media(params SegmentRun[] runs) => new("X", runs, 3.0, false, true);

    private static readonly Func<int, bool> NothingCached = _ => false;

    [Fact]
    public void Playhead_first_then_forward_then_the_rest_nearest_first()
    {
        var order = ChunkPlan.Order(ChunkPlan.Range(Media(new SegmentRun(1, 10)), 1, 10), playheadChunk: 6, NothingCached);
        Assert.Equal([6, 7, 8, 9, 10, 5, 4, 3, 2, 1], order);
    }

    [Fact]
    public void Playhead_before_the_selection_starts_at_its_first_chunk()
    {
        var order = ChunkPlan.Order(ChunkPlan.Range(Media(new SegmentRun(1, 10)), 4, 7), playheadChunk: 1, NothingCached);
        Assert.Equal([4, 5, 6, 7], order);
    }

    [Fact]
    public void Playhead_after_the_selection_walks_backwards()
    {
        var order = ChunkPlan.Order(ChunkPlan.Range(Media(new SegmentRun(1, 10)), 4, 7), playheadChunk: 99, NothingCached);
        Assert.Equal([7, 6, 5, 4], order);
    }

    [Fact]
    public void Cached_chunks_are_skipped()
    {
        HashSet<int> cached = [6, 8, 2];
        var order = ChunkPlan.Order(ChunkPlan.Range(Media(new SegmentRun(1, 10)), 1, 10), 6, c => cached.Contains(c));
        Assert.Equal([7, 9, 10, 5, 4, 3, 1], order);
    }

    [Fact]
    public void Everything_cached_gives_an_empty_plan() =>
        Assert.Empty(ChunkPlan.Order([1, 2, 3], 1, _ => true));

    [Fact]
    public void A_seek_reprioritises_the_remaining_chunks()
    {
        var sel = ChunkPlan.Range(Media(new SegmentRun(1, 12)), 1, 12);
        HashSet<int> cached = [1, 2, 3];
        var before = ChunkPlan.Order(sel, 1, c => cached.Contains(c));
        Assert.Equal([4, 5, 6, 7, 8, 9, 10, 11, 12], before);

        cached.Add(4);                                                 // 4 arrived, then the user seeks to chunk 9
        var after = ChunkPlan.Order(sel, 9, c => cached.Contains(c));
        Assert.Equal([9, 10, 11, 12, 8, 7, 6, 5], after);
    }

    [Fact]
    public void Selection_is_deduplicated_and_order_of_input_is_irrelevant()
    {
        var order = ChunkPlan.Order([5, 3, 3, 4, 5], 3, NothingCached);
        Assert.Equal([3, 4, 5], order);
    }

    [Fact]
    public void Range_only_contains_chunks_present_in_the_session()
    {
        var media = Media(new SegmentRun(1, 3), new SegmentRun(6, 8));
        Assert.Equal([2, 3, 6], ChunkPlan.Range(media, 2, 6));
    }

    [Fact]
    public void Whole_is_every_chunk_of_the_session()
    {
        var media = Media(new SegmentRun(5, 6), new SegmentRun(9, 9));
        Assert.Equal([5, 6, 9], ChunkPlan.Whole(media));
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(2.99, 1)]
    [InlineData(3.0, 2)]
    [InlineData(100.0, 34)]
    public void ChunkAt_maps_media_time_to_the_chunk_number(double seconds, int chunk) =>
        Assert.Equal(chunk, ChunkPlan.ChunkAt(seconds, 3.0));

    [Fact]
    public void Around_markers_covers_30_seconds_each_side_of_every_marker()
    {
        var media = Media(new SegmentRun(1, 100));
        // marker at 150 s: [120, 180] -> chunks 41..61 (chunk n covers [(n-1)*3, n*3)); 180 s is the start of chunk 61
        var sel = ChunkPlan.AroundMarkers(media, [150.0], 30.0);
        Assert.Equal(Enumerable.Range(41, 21), sel);
    }

    [Fact]
    public void Around_markers_merges_overlaps_clamps_to_the_session_and_sorts()
    {
        var media = Media(new SegmentRun(10, 30));                      // media time 27 s .. 90 s
        var sel = ChunkPlan.AroundMarkers(media, [60.0, 5.0, 66.0], 30.0);
        // 5 s: [-25,35] -> chunks 1..12 -> only 10..12 exist; 60/66 s: [30,96] -> chunks 11..33 -> 11..30
        Assert.Equal(Enumerable.Range(10, 21), sel);
        Assert.Equal(sel.Distinct(), sel);
    }

    [Fact]
    public void Around_markers_on_a_clip_uses_the_clip_axis_shift()
    {
        // A saved clip: chunks 1..10 whose chunk 1 sits at tfdt 585 s (shift 585). A marker at 600 s ± 3 s -> [597, 603] -> chunks 5..7.
        var clip = new SessionMedia("X", [new SegmentRun(1, 10)], 3.0, false, false, AxisShiftSeconds: 585);
        Assert.Equal([5, 6, 7], ChunkPlan.AroundMarkers(clip, [600.0], 3.0));
    }

    [Fact]
    public void Around_markers_with_no_markers_selects_nothing() =>
        Assert.Empty(ChunkPlan.AroundMarkers(Media(new SegmentRun(1, 10)), [], 30.0));

    [Fact]
    public void Around_moments_takes_the_whole_range_plus_the_margins()
    {
        var media = Media(new SegmentRun(1, 100));
        // a range 90..98 s with 30 s before and 6 s after: [60, 104] -> chunks 21..35 (104 s is inside chunk 35)
        Assert.Equal(Enumerable.Range(21, 15), ChunkPlan.AroundMoments(media, [(90.0, 98.0)], 30.0, 6.0));
    }

    [Fact]
    public void Around_moments_clamps_to_chunks_that_exist_and_merges_neighbours()
    {
        var media = Media(new SegmentRun(5, 7), new SegmentRun(20, 22));  // chunks 5..7 and 20..22; a gap in between
        var sel = ChunkPlan.AroundMoments(media, [(15.0, 15.0), (60.0, 60.0), (16.0, 16.0)], 3.0, 3.0);
        Assert.Equal([5, 6, 7, 20, 21, 22], sel);
        Assert.Empty(ChunkPlan.AroundMoments(media, [(1000.0, 1000.0)], 30.0, 30.0));
    }

    [Fact]
    public void Around_moments_rejects_negative_margins() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlan.AroundMoments(Media(new SegmentRun(1, 10)), [(1.0, 1.0)], -1.0, 0.0));

    [Fact]
    public void Plan_around_a_marker_orders_from_the_playhead()
    {
        var media = Media(new SegmentRun(1, 100));
        var sel = ChunkPlan.AroundMarkers(media, [150.0], 3.0);         // [147,153] -> chunks 50..52 (153 s starts chunk 52)
        Assert.Equal([50, 51, 52], sel);
        Assert.Equal([51, 52, 50], ChunkPlan.Order(sel, 51, NothingCached));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(10, 2)]
    public void Parallelism_is_2_to_3_and_drops_when_retries_rise(int recentRetries, int expected) =>
        Assert.Equal(expected, ChunkPlan.SuggestedParallelism(recentRetries));

    [Fact]
    public void Negative_retries_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlan.SuggestedParallelism(-1));
}
