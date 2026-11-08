// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Views.Player;

namespace SteamPoweredPlayer.Tests.Player;

public sealed class TimelineRulerTests
{
    private const double Label = 56;

    private static IReadOnlyList<RulerTick> Majors(RulerLayout l) => l.Ticks.Where(t => t.Major).ToList();

    [Theory]
    [InlineData(0, 3600, 1000)]
    [InlineData(0, 120, 1000)]
    [InlineData(0, 2, 1000)]
    [InlineData(100, 160, 1900)]
    [InlineData(0, 36000, 640)]
    [InlineData(5000, 5030, 1400)]
    public void Labels_never_overlap_and_sit_about_a_label_apart(double t0, double t1, double width)
    {
        var l = TimelineRuler.Layout(t0, t1, width, Label, 60);
        var majors = Majors(l);
        Assert.True(majors.Count >= 1);
        var min = TimelineRuler.MinSpacing(Label);
        for (var i = 1; i < majors.Count; i++)
        {
            var gap = majors[i].X - majors[i - 1].X;
            Assert.True(gap >= min - 1e-6, $"gap {gap} < {min}");
            Assert.True(gap <= min * 2.5 + 1e-6, $"gap {gap} too wide");
        }
    }

    [Theory]
    [InlineData(0, 3600, 1000)]
    [InlineData(0, 120, 1000)]
    [InlineData(0, 2, 1000)]
    [InlineData(0, 36000, 640)]
    public void Minor_ticks_are_five_or_ten_per_major_and_at_least_six_px_apart(double t0, double t1, double width)
    {
        var l = TimelineRuler.Layout(t0, t1, width, Label, 60);
        if (!l.Frames) Assert.Contains(l.MinorPerMajor, new[] { 5, 10 });
        for (var i = 1; i < l.Ticks.Count; i++) Assert.True(l.Ticks[i].X - l.Ticks[i - 1].X >= TimelineRuler.MinMinorPx - 1e-6);
    }

    [Fact]
    public void Major_step_follows_the_ladder_as_the_window_zooms()
    {
        double Step(double len) => TimelineRuler.Layout(0, len, 1000, Label, 60).MajorStepSeconds;
        Assert.Equal(10, Step(120));            // 8.3 px/s -> 10 s is 83 px
        Assert.Equal(60, Step(600));            // 1.67 px/s
        Assert.Equal(7200, Step(1_000_000));    // top rung
        Assert.Equal(1, Step(12));
        Assert.True(Step(2) < 1, "max zoom steps in frames");
    }

    [Fact]
    public void Frames_only_when_deeply_zoomed_and_labelled_with_the_frame()
    {
        var wide = TimelineRuler.Layout(0, 600, 1000, Label, 60);
        Assert.False(wide.Frames);
        Assert.All(Majors(wide), t => Assert.DoesNotContain('.', t.Label!));

        var deep = TimelineRuler.Layout(10, 12, 1000, Label, 60);
        Assert.True(deep.Frames);
        Assert.Equal(10.0 / 60, deep.MajorStepSeconds, 9);
        Assert.Contains(Majors(deep), t => t.Label == "0:10.01");
        Assert.Contains(Majors(deep), t => t.Label == "0:10.11");
    }

    [Fact]
    public void Label_format_is_MSS_under_an_hour_and_HMMSS_above()
    {
        Assert.Equal("0:00", TimelineRuler.Label(0, false, 60));
        Assert.Equal("1:05", TimelineRuler.Label(65, false, 60));
        Assert.Equal("59:59", TimelineRuler.Label(3599, false, 60));
        Assert.Equal("1:00:00", TimelineRuler.Label(3600, false, 60));
        Assert.Equal("2:03:09", TimelineRuler.Label(7389, false, 60));
    }

    [Fact]
    public void Ticks_cover_the_window_x_maps_linearly_and_the_first_tick_is_not_negative_time()
    {
        var l = TimelineRuler.Layout(0, 60, 1200, Label, 60);
        Assert.Equal(0, l.Ticks[0].Seconds);
        Assert.Equal(0, l.Ticks[0].X, 6);
        Assert.Equal(1200, l.Ticks[^1].X, 6);
        var scrolled = TimelineRuler.Layout(30, 90, 1200, Label, 60);
        Assert.All(scrolled.Ticks, t => Assert.Equal((t.Seconds - 30) * 20, t.X, 6));
        Assert.True(scrolled.Ticks[0].X <= 0, "a tick just left of the window keeps its label sliding in");
    }

    [Theory]
    [InlineData(0, 0, 1000)]
    [InlineData(5, 5, 1000)]
    [InlineData(0, 10, 0)]
    [InlineData(double.NaN, 10, 1000)]
    public void Degenerate_windows_give_no_ticks(double t0, double t1, double width) =>
        Assert.Empty(TimelineRuler.Layout(t0, t1, width, Label, 60).Ticks);

    [Fact]
    public void Layout_is_deterministic_so_it_can_be_cached()
    {
        var a = TimelineRuler.Layout(3, 57, 913, Label, 59.94);
        var b = TimelineRuler.Layout(3, 57, 913, Label, 59.94);
        Assert.Equal(a.Ticks, b.Ticks);
    }
}
