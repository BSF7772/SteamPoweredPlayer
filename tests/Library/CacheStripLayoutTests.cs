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

    // ---- L1: the clock-anchored layout (injected now) ----

    private static readonly DateTimeOffset Now = new(2025, 3, 4, 9, 0, 0, TimeSpan.Zero);

    private static StripSpan Ago(double startHoursAgo, double lengthMin) =>
        new(Now.AddHours(-startHoursAgo), Now.AddHours(-startHoursAgo).AddMinutes(lengthMin));

    [Fact]
    public void Anchored_all_recent_uses_the_whole_width_and_ends_at_now()
    {
        var l = CacheStripLayout.Build([Ago(5, 60), Ago(2, 30)], 1000, Utc, Now);
        Assert.Null(l.Cutoff);
        Assert.False(l.RecentEmpty);
        Assert.Equal(1000, l.NowX, 6);
        Assert.Equal(0, l.Blocks[0].X0, 6);
        Assert.True(l.Blocks[^1].X1 < 1000);                           // the quiet time since the last session is drawn
        Assert.Equal("Now", l.Ticks[^1].Label);
        Assert.Equal(1000, l.Ticks[^1].X, 6);
    }

    [Fact]
    public void Anchored_start_label_has_the_exact_start_and_the_age()
    {
        // it is 09:00; the session started yesterday 23:00. Not "from the future": Yesterday 23:00, 10 h ago
        var l = CacheStripLayout.Build([new(Now.AddHours(-10), Now.AddHours(-9))], 900, Utc, Now);
        var t = l.Ticks[0];
        Assert.Equal("Yesterday 23:00", t.Label);
        Assert.Equal("10 h ago", t.Relative);
        Assert.True(t.IsEdge);
    }

    [Fact]
    public void Anchored_mixed_old_sessions_go_to_the_left_quarter_with_a_break()
    {
        var l = CacheStripLayout.Build([Ago(80, 60), Ago(50, 40), Ago(6, 120), Ago(1, 30)], 1000, Utc, Now);
        var cut = Assert.NotNull(l.Cutoff);
        Assert.True(cut.X0 <= 250 + 1e-6);
        Assert.Equal(CacheStripLayout.CutoffBreakWidth, cut.X1 - cut.X0, 6);
        var old = l.Blocks.Where(b => b.Index < 2).ToList();
        var recent = l.Blocks.Where(b => b.Index >= 2).ToList();
        Assert.All(old, b => Assert.True(b.X1 <= cut.X0 + 1e-6));
        Assert.All(recent, b => Assert.True(b.X0 >= cut.X1 - 1e-6));
        Assert.Equal(1000, l.NowX, 6);
        Assert.Equal("Now", l.Ticks[^1].Label);
    }

    [Fact]
    public void Anchored_all_old_fills_the_left_quarter_and_says_the_last_day_is_empty()
    {
        var l = CacheStripLayout.Build([Ago(80, 60), Ago(50, 40)], 1000, Utc, Now);
        Assert.True(l.RecentEmpty);
        var cut = Assert.NotNull(l.Cutoff);
        Assert.True(l.Blocks.All(b => b.X1 <= cut.X0 + 1e-6));
        Assert.Equal(1000, l.NowX, 6);
        Assert.Equal("Now", l.Ticks[^1].Label);
    }

    [Fact]
    public void Anchored_a_session_ending_exactly_24h_ago_is_still_recent()
    {
        var l = CacheStripLayout.Build([new(Now.AddHours(-25), Now.AddHours(-24))], 800, Utc, Now);
        Assert.Null(l.Cutoff);
        Assert.False(l.RecentEmpty);
    }

    [Fact]
    public void Anchored_midnight_crossing_is_marked_and_the_labels_name_the_day()
    {
        // now 09:00 on Mar 4; a session 22:00-23:30 on Mar 3 and one 07:00-08:30 on Mar 4
        var a = new StripSpan(Now.AddHours(-11), Now.AddHours(-9.5));
        var b = new StripSpan(Now.AddHours(-2), Now.AddHours(-0.5));
        var l = CacheStripLayout.Build([a, b], 1000, Utc, Now);
        var m = Assert.Single(l.Midnights);
        Assert.Equal("Tue Mar 4", m.Label);
        Assert.True(m.X > l.Blocks[0].X1 && m.X < l.Blocks[1].X0);
        Assert.Equal("Yesterday 22:00", l.Ticks[0].Label);
        Assert.Contains(l.Ticks, t => t.Label == "Today 07:00");
    }

    [Fact]
    public void Anchored_without_a_midnight_in_range_has_no_midnight_line()
    {
        var l = CacheStripLayout.Build([Ago(5, 60)], 800, Utc, Now);
        Assert.Empty(l.Midnights);
    }

    [Fact]
    public void Anchored_a_span_ending_in_the_future_never_draws_past_the_axis_end()
    {
        var l = CacheStripLayout.Build([new(Now.AddMinutes(-30), Now.AddMinutes(20))], 800, Utc, Now);
        Assert.Equal(800, l.NowX, 6);
        Assert.True(l.Blocks[^1].X1 <= 800 + 1e-6);
        Assert.Equal("just now", CacheStripLayout.Age(TimeSpan.FromMinutes(-5)));
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(10 * 3600, "10 h ago")]
    [InlineData(3 * 86400 + 100, "3 d ago")]
    public void Age_reads_naturally(int seconds, string expected) =>
        Assert.Equal(expected, CacheStripLayout.Age(TimeSpan.FromSeconds(seconds)));

    private static TimeZoneInfo Berlin() =>
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Berlin", out var z) ? z : TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    [Fact]
    public void Day_labels_follow_the_local_calendar_day_not_24_hour_blocks()
    {
        var berlin = Berlin();                                                 // UTC+1 in March: local 00:30 is 23:30 UTC the day before
        var now = new DateTimeOffset(2025, 3, 4, 8, 0, 0, TimeSpan.Zero);      // local 09:00 on Mar 4
        Assert.Equal("Today 00:30", CacheStripLayout.StartLabel(new(2025, 3, 3, 23, 30, 0, TimeSpan.Zero), now, berlin));
        Assert.Equal("Yesterday 23:30", CacheStripLayout.StartLabel(new(2025, 3, 3, 22, 30, 0, TimeSpan.Zero), now, berlin));
        Assert.Equal("Sat Mar 1 12:00", CacheStripLayout.StartLabel(new(2025, 3, 1, 11, 0, 0, TimeSpan.Zero), now, berlin));
        Assert.Equal("Tue Dec 31, 2024 12:00", CacheStripLayout.StartLabel(new(2024, 12, 31, 11, 0, 0, TimeSpan.Zero), now, berlin));
    }

    [Fact]
    public void Midnights_survive_a_dst_change()
    {
        var berlin = Berlin();                                                 // clocks go forward Sun 2025-03-30 02:00 -> 03:00
        var now = new DateTimeOffset(2025, 3, 30, 10, 0, 0, TimeSpan.FromHours(2));
        var a = new StripSpan(new(2025, 3, 29, 20, 0, 0, TimeSpan.FromHours(1)), new(2025, 3, 29, 22, 0, 0, TimeSpan.FromHours(1)));
        var b = new StripSpan(new(2025, 3, 30, 8, 0, 0, TimeSpan.FromHours(2)), new(2025, 3, 30, 9, 30, 0, TimeSpan.FromHours(2)));
        var l = CacheStripLayout.Build([a, b], 1000, berlin, now);
        var m = Assert.Single(l.Midnights);
        Assert.Equal("Sun Mar 30", m.Label);
        Assert.Equal(new DateTimeOffset(2025, 3, 29, 23, 0, 0, TimeSpan.Zero), CacheStripLayout.LocalMidnight(new DateTime(2025, 3, 30), berlin));
        Assert.Equal("Yesterday 20:00", l.Ticks[0].Label);
        Assert.Contains(l.Ticks, t => t.Label == "Today 08:00");
    }

    [Fact]
    public void A_midnight_the_clocks_skipped_is_the_next_valid_time()
    {
        // a zone whose clocks jump 00:00 -> 01:00 on Jun 15
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 6, 15);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 12, 1);
        var zone = TimeZoneInfo.CreateCustomTimeZone("jump", TimeSpan.Zero, "jump", "jump", "jump-dst",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), TimeSpan.FromHours(1), start, end)]);
        var mid = CacheStripLayout.LocalMidnight(new DateTime(2025, 6, 15), zone);
        Assert.Equal(1, mid.Hour);                                             // 00:00 does not exist that day: 01:00
        Assert.Equal(15, mid.Day);
    }

    [Fact]
    public void Anchored_empty_inputs_give_the_empty_layout()
    {
        Assert.Same(StripLayout.Empty, CacheStripLayout.Build([], 800, Utc, Now));
        Assert.Same(StripLayout.Empty, CacheStripLayout.Build([Ago(1, 5)], 0, Utc, Now));
    }
}
