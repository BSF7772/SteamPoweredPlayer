// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;
using Xunit;

namespace SteamPoweredPlayer.Tests.Playback;

public sealed class MarkerKindTests
{
    private static TimelineEntry Entry(string? icon, TimelineEntryType type = TimelineEntryType.Event, long durationMs = 0) =>
        new(null, 0, type, icon ?? "", null, null, icon, null, durationMs, false, null, [], null, null);

    [Theory]
    [InlineData("cs2_death", MarkerKind.Death)]
    [InlineData("cs2_gun_kill", MarkerKind.Kill)]
    [InlineData("cs2_knife_kill", MarkerKind.Kill)]
    [InlineData("cs2_multi_kill", MarkerKind.MultiKill)]
    [InlineData("cs2_double_kill", MarkerKind.MultiKill)]
    [InlineData("cs2_bomb_plant", MarkerKind.Objective)]
    [InlineData("steam_screenshot", MarkerKind.Screenshot)]
    [InlineData("something_new", MarkerKind.Other)]
    public void Classifies_by_icon(string icon, MarkerKind expected) => Assert.Equal(expected, MarkerKinds.Classify(Entry(icon)));

    [Fact]
    public void Entry_type_wins_for_phases_screenshots_and_achievements()
    {
        Assert.Equal(MarkerKind.Phase, MarkerKinds.Classify(Entry("x", TimelineEntryType.Phase)));
        Assert.Equal(MarkerKind.Screenshot, MarkerKinds.Classify(Entry("x", TimelineEntryType.Screenshot)));
        Assert.Equal(MarkerKind.Achievement, MarkerKinds.Classify(Entry("x", TimelineEntryType.Achievement)));
        Assert.False(MarkerKinds.ShowOnLane(MarkerKind.Phase));
        Assert.True(MarkerKinds.ShowOnLane(MarkerKind.Kill));
    }

    [Fact]
    public void A_kill_with_duration_is_a_multi_kill() => Assert.Equal(MarkerKind.MultiKill, MarkerKinds.Classify(Entry("cs2_gun_kill", durationMs: 8000)));
}

public sealed class MarkerNavigationTests
{
    private static Marker At(double start) =>
        new(new TimelineEntry(null, 0, TimelineEntryType.Event, "e", null, null, null, null, 0, false, null, [], null, null), start, null, MarkerAlignment.Exact);

    private static readonly Marker[] List = [At(10), At(30), At(31), At(60)];

    [Fact]
    public void Landing_is_early_and_never_negative()
    {
        Assert.Equal(7.5, MarkerNavigation.LandingSeconds(At(10)));
        Assert.Equal(0, MarkerNavigation.LandingSeconds(At(1)));
    }

    [Fact]
    public void Next_walks_the_list_even_after_landing_on_an_event()
    {
        var m = MarkerNavigation.Next(List, 0);
        Assert.Equal(10, m!.StartSeconds);
        m = MarkerNavigation.Next(List, MarkerNavigation.LandingSeconds(m));      // we are at the landing point of the first one
        Assert.Equal(30, m!.StartSeconds);
        Assert.Null(MarkerNavigation.Next(List, 58));
    }

    [Fact]
    public void Previous_goes_back_one_event_and_replays_the_current_one_later_in_it()
    {
        Assert.Null(MarkerNavigation.Previous(List, 5));
        Assert.Equal(10, MarkerNavigation.Previous(List, 12)!.StartSeconds);              // inside the first event: back to its start
        Assert.Equal(10, MarkerNavigation.Previous(List, MarkerNavigation.LandingSeconds(At(30)))!.StartSeconds);   // landed on the second: the first
        Assert.Equal(60, MarkerNavigation.Previous(List, 100)!.StartSeconds);
    }
}

public sealed class PlaybackMetricsTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => 1000;       // 1 tick = 1 ms
    }

    [Fact]
    public void Measures_from_begin_to_complete_and_ignores_unrequested_restarts()
    {
        var clock = new Clock();
        var m = new PlaybackMetrics(clock);
        Assert.Null(m.Complete());
        m.Begin(MetricKind.Seek);
        clock.Ticks = 120;
        var s = m.Complete()!;
        Assert.Equal(MetricKind.Seek, s.Kind);
        Assert.Equal(120, s.Milliseconds, 3);
        Assert.Null(m.Complete());
        Assert.Equal(120, m.Last(MetricKind.Seek)!.Milliseconds, 3);
        Assert.Null(m.Last(MetricKind.Open));
    }

    [Fact]
    public void The_first_pending_request_keeps_its_start_but_an_open_replaces_a_seek()
    {
        var clock = new Clock();
        var m = new PlaybackMetrics(clock);
        m.Begin(MetricKind.Seek);
        clock.Ticks = 50;
        m.Begin(MetricKind.Seek);                 // a burst: still one measurement from the first
        clock.Ticks = 90;
        Assert.Equal(90, m.Complete()!.Milliseconds, 3);

        m.Begin(MetricKind.Seek);
        clock.Ticks = 100;
        m.Begin(MetricKind.Open);                 // open wins
        clock.Ticks = 340;
        var open = m.Complete()!;
        Assert.Equal(MetricKind.Open, open.Kind);
        Assert.Equal(240, open.Milliseconds, 3);
    }

    [Fact]
    public void Summary_reports_percentiles_and_capacity_is_bounded()
    {
        var clock = new Clock();
        var m = new PlaybackMetrics(clock, capacity: 5);
        for (var i = 1; i <= 8; i++)
        {
            m.Begin(MetricKind.Seek);
            clock.Ticks += i * 10;
            m.Complete();
        }
        Assert.Equal(5, m.Samples.Count);
        var sum = m.Summary(MetricKind.Seek);
        Assert.Equal(5, sum.Count);
        Assert.Equal(80, sum.Max, 3);
        Assert.Equal(0, m.Summary(MetricKind.Open).Count);
    }
}

public sealed class ResumePositionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spp-resume-" + Guid.NewGuid().ToString("N")[..8]);
    private string File_ => Path.Combine(_dir, "positions.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Round_trips_through_the_file_and_only_writes_when_dirty()
    {
        var a = new ResumePositions(File_);
        Assert.False(a.Flush());
        a.Set("video/bg_1_x", 42.5, 120);
        Assert.True(a.Flush());
        Assert.False(a.Flush());

        var b = new ResumePositions(File_);
        Assert.Equal(42.5, b.Get("video/bg_1_x")!.MediaSeconds);
        Assert.Equal(120, b.Get("video/bg_1_x")!.DurationSeconds);
        Assert.Null(b.Get("other"));
    }

    [Fact]
    public void A_damaged_file_means_no_positions()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ not json");
        Assert.Equal(0, new ResumePositions(File_).Count);
    }

    [Fact]
    public void Keeps_the_newest_entries_when_over_the_cap()
    {
        var p = new ResumePositions(File_);
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < ResumePositions.MaxEntries + 3; i++) p.Set("k" + i, 10, 100, t0.AddSeconds(i));
        Assert.Equal(ResumePositions.MaxEntries, p.Count);
        Assert.Null(p.Get("k0"));
        Assert.NotNull(p.Get("k" + (ResumePositions.MaxEntries + 2)));
    }

    [Theory]
    [InlineData(0, 100, 3, null)]          // within 5 s of the start
    [InlineData(0, 100, 5, 5.0)]
    [InlineData(0, 100, 50, 50.0)]
    [InlineData(0, 100, 96, null)]         // within 5 s of the end
    [InlineData(300, 400, 310, 310.0)]     // media axis does not start at 0
    [InlineData(300, 400, 298, null)]      // trimmed away
    public void Resume_rules(double mediaStart, double mediaEnd, double saved, double? expected)
    {
        var e = new ResumeEntry(saved, mediaEnd - mediaStart, DateTimeOffset.UtcNow);
        Assert.Equal(expected, ResumePositions.ResumeMediaSeconds(e, mediaStart, mediaEnd));
    }

    [Fact]
    public void No_entry_means_no_resume() => Assert.Null(ResumePositions.ResumeMediaSeconds(null, 0, 100));
}

public sealed class AbLoopTests
{
    [Fact]
    public void Cycle_sets_a_then_b_then_clears()
    {
        var l = new AbLoop();
        Assert.Equal("A set", l.Cycle(10));
        Assert.False(l.Active);
        Assert.Equal("B must be after A", l.Cycle(10));
        Assert.Equal("Looping A–B", l.Cycle(20));
        Assert.True(l.Active);
        Assert.Equal("Loop cleared", l.Cycle(30));
        Assert.Null(l.A);
        Assert.Null(l.B);
    }

    [Fact]
    public void Jumps_only_when_active_and_at_or_past_b()
    {
        var l = new AbLoop();
        l.SetA(10);
        Assert.False(l.ShouldJump(100));
        Assert.True(l.SetB(20));
        Assert.False(l.ShouldJump(19.9));
        Assert.True(l.ShouldJump(20));
    }

    [Fact]
    public void Moving_a_past_b_drops_b_and_b_needs_an_a()
    {
        var l = new AbLoop();
        Assert.False(l.SetB(5));
        l.SetA(10);
        l.SetB(20);
        l.SetA(25);
        Assert.Null(l.B);
        Assert.False(l.Active);
    }
}
