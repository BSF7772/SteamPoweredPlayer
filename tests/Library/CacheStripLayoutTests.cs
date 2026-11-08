// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Views;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class CacheStripLayoutTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset T0 = new(2025, 3, 4, 10, 0, 0, TimeSpan.Zero);

    private static StripSpan Span(double startMin, double lengthMin) =>
        new(T0.AddMinutes(startMin), T0.AddMinutes(startMin + lengthMin));

    [Fact]
    public void One_session_fills_the_strip_and_is_labelled_with_its_absolute_start_and_end()
    {
        var l = CacheStripLayout.Build([Span(0, 90)], 800, Utc);
        var b = Assert.Single(l.Blocks);
        Assert.Equal(0, b.X0, 6);
        Assert.Equal(800, b.X1, 6);
        Assert.Empty(l.Gaps);
        Assert.Equal("10:00", l.Ticks[0].Label);
        Assert.Equal("11:30", l.Ticks[^1].Label);
        Assert.True(l.Ticks[0].IsEdge && l.Ticks[^1].IsEdge);
        // round times in between, in order, spaced apart
        Assert.Contains(l.Ticks, t => !t.IsEdge);
        for (var i = 1; i < l.Ticks.Count; i++) Assert.True(l.Ticks[i].X - l.Ticks[i - 1].X >= CacheStripLayout.MinLabelSpacing);
        Assert.DoesNotContain(l.Ticks, t => t.Label == "relative" || t.Label.StartsWith('−'));
    }

    [Fact]
    public void A_round_time_next_to_an_edge_in_the_same_minute_is_not_labelled_twice()
    {
        // 21:52:50 - 21:59:35 on 1100 px: the round 21:59 tick and the 21:59:35 end edge were both drawn as "21:59"
        var s = new StripSpan(T0.AddHours(11).AddMinutes(52).AddSeconds(50), T0.AddHours(11).AddMinutes(59).AddSeconds(35));
        var labels = CacheStripLayout.Build([s], 1100, Utc).Ticks.Select(t => t.Label).ToList();
        Assert.Equal(labels.Distinct().Count(), labels.Count);
        Assert.Equal("21:52", labels[0]);
        Assert.Equal("21:59", labels[^1]);
    }

    [Fact]
    public void Several_sessions_are_separate_blocks_with_a_visible_gap_in_time_order()
    {
        // input order is not time order; the result keeps the input index
        var l = CacheStripLayout.Build([Span(60, 20), Span(0, 30)], 1000, Utc);
        Assert.Equal([1, 0], l.Blocks.Select(b => b.Index));
        var gap = Assert.Single(l.Gaps);
        Assert.True(gap.X0 >= l.Blocks[0].X1 - 1e-9 && gap.X1 <= l.Blocks[1].X0 + 1e-9);
        Assert.True(l.Blocks[1].X0 - l.Blocks[0].X1 >= CacheStripLayout.MinGapWidth);
        Assert.Equal(TimeSpan.FromMinutes(30), gap.Length);
        Assert.False(gap.Compressed);                                  // 30 min gap vs 50 min recorded: drawn to scale
        // to scale: the 30 min session is wider than the 20 min one
        Assert.True(l.Blocks[0].X1 - l.Blocks[0].X0 > l.Blocks[1].X1 - l.Blocks[1].X0);
        Assert.Equal(1000, l.Blocks[^1].X1, 6);
    }

    [Fact]
    public void A_gap_of_days_is_compressed_and_labels_carry_the_date()
    {
        // yesterday 22:00-23:30 and today 10:00-11:00: the strip crosses midnight
        var l = CacheStripLayout.Build([Span(-12 * 60, 90), Span(0, 60)], 900, Utc);
        var gap = Assert.Single(l.Gaps);
        Assert.True(gap.Compressed);
        Assert.Equal(CacheStripLayout.BreakWidth, gap.X1 - gap.X0, 6);
        // the blocks keep most of the width
        Assert.True(l.Blocks.Sum(b => b.X1 - b.X0) > 800);
        Assert.Equal("Mar 3 22:00", l.Ticks[0].Label);
        Assert.Contains(l.Ticks, t => t.Label.StartsWith("Mar 4 ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tiny_session_next_to_a_long_one_stays_clickable()
    {
        var l = CacheStripLayout.Build([Span(0, 600), Span(601, 0.05)], 700, Utc);
        Assert.True(l.Blocks[1].X1 - l.Blocks[1].X0 >= CacheStripLayout.MinBlockWidth - 1e-6);
        Assert.Equal(700, l.Blocks[^1].X1, 6);
    }

    [Fact]
    public void XOf_places_a_time_inside_its_block_and_clamps_outside()
    {
        var l = CacheStripLayout.Build([Span(0, 60)], 600, Utc);
        var b = l.Blocks[0];
        Assert.Equal(300, CacheStripLayout.XOf(b, T0.AddMinutes(30)), 6);
        Assert.Equal(0, CacheStripLayout.XOf(b, T0.AddMinutes(-5)), 6);
        Assert.Equal(600, CacheStripLayout.XOf(b, T0.AddMinutes(90)), 6);
    }

    [Fact]
    public void Nothing_to_draw_gives_an_empty_layout()
    {
        Assert.Same(StripLayout.Empty, CacheStripLayout.Build([], 800, Utc));
        Assert.Same(StripLayout.Empty, CacheStripLayout.Build([Span(0, 5)], 0, Utc));
    }
}
