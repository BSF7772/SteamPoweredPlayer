// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using SteamPoweredPlayer.Core.Diagnostics;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Diagnostics;

/// <summary>The pure rules behind <c>--realcheck</c> (plan 2026-11-07 §4.3, D-TEST, D-RAM, F2/F3).</summary>
public sealed class RealCheckRulesTests
{
    // ---- picking the recordings ------------------------------------------------------------------------------------

    private static RecordingEntry Entry(RecordingKind kind, string folder, int chunks, DateTimeOffset start, bool live = false, int first = 1) =>
        new(kind, "1", start, folder, chunks == 0 ? [] : [new SegmentRun(first, first + chunks - 1)], 3, chunks * 3.0, live, kind == RecordingKind.CacheSession);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_shortest_saved_clip_long_enough_for_the_checks_is_picked()
    {
        var list = new[]
        {
            Entry(RecordingKind.Clip, "clips/a", 20, T0),
            Entry(RecordingKind.Clip, "clips/b", 4, T0),       // 12 s: the shortest one that fits
            Entry(RecordingKind.Clip, "clips/c", 1, T0),       // 3 s: too short for +5 s and A-B
            Entry(RecordingKind.Clip, "clips/d", 0, T0),       // not playable
            Entry(RecordingKind.CacheSession, "video/x", 2, T0),
        };
        Assert.Equal("clips/b", RealCheckRules.ShortestClip(list)?.RelativeFolder);
        Assert.Null(RealCheckRules.ShortestClip([list[2], list[3], list[4]]));
    }

    [Fact]
    public void The_live_cache_session_wins_else_the_newest()
    {
        var older = Entry(RecordingKind.CacheSession, "video/old", 30, T0);
        var newer = Entry(RecordingKind.CacheSession, "video/new", 30, T0.AddHours(1));
        var live = Entry(RecordingKind.CacheSession, "video/live", 10, T0.AddMinutes(5), live: true);
        Assert.Equal("video/live", RealCheckRules.ActiveCache([older, live, newer])?.RelativeFolder);
        Assert.Equal("video/new", RealCheckRules.ActiveCache([older, newer, Entry(RecordingKind.Clip, "clips/a", 9, T0.AddDays(1))])?.RelativeFolder);
        Assert.Null(RealCheckRules.ActiveCache([Entry(RecordingKind.Clip, "clips/a", 9, T0)]));
    }

    // ---- frame tolerance -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(60, 10.0, 10.016, true)]
    [InlineData(60, 10.0, 10.02, false)]
    [InlineData(30, 10.0, 9.967, true)]
    [InlineData(30, 10.0, double.NaN, false)]
    public void Within_one_frame(double fps, double expected, double actual, bool ok) =>
        Assert.Equal(ok, RealCheckRules.WithinFrames(expected, actual, fps));

    // ---- A-B loop --------------------------------------------------------------------------------------------------

    [Fact]
    public void An_ab_loop_that_wraps_and_never_overshoots_passes()
    {
        var samples = new List<double>();
        for (var lap = 0; lap < 2; lap++)
            for (var t = 1.0; t < 3.0; t += 0.02) samples.Add(t);
        var r = RealCheckRules.EvaluateAbLoop(samples, 1, 3, 60);
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(1, r.Wraps);
    }

    [Fact]
    public void An_ab_loop_that_runs_past_b_or_never_wraps_fails()
    {
        var past = Enumerable.Range(0, 200).Select(i => 1 + i * 0.02).ToList();         // 1 .. 5 s, no wrap
        var r = RealCheckRules.EvaluateAbLoop(past, 1, 3, 60);
        Assert.False(r.Ok);
        Assert.Equal(0, r.Wraps);
        Assert.True(r.MaxPastB > 1.9);
        Assert.False(RealCheckRules.EvaluateAbLoop([], 1, 3, 60).Ok);
        // wraps, but to the wrong place (e.g. the head of the session instead of A)
        var wrong = new List<double> { 1, 2, 2.9, 0.1, 0.5 };
        Assert.False(RealCheckRules.EvaluateAbLoop(wrong, 1, 3, 60).Ok);
    }

    // ---- RAM (D-RAM) -----------------------------------------------------------------------------------------------

    [Fact]
    public void Ram_gate_is_base_plus_expected_cache_plus_15_percent()
    {
        // base 300 MB + cache 100 MB = 400 MB, +15 % = 460 MB
        Assert.Equal(CheckStatus.Pass, BudgetRules.RamGate("i", "n", 459.9, 300, 100).Status);
        Assert.Equal(CheckStatus.Pass, BudgetRules.RamGate("i", "n", 460, 300, 100).Status);
        Assert.Equal(CheckStatus.Fail, BudgetRules.RamGate("i", "n", 460.1, 300, 100).Status);
        Assert.Equal(CheckStatus.Fail, BudgetRules.RamGate("i", "n", double.NaN, 300, 100).Status);
        Assert.Contains("limit 460 MB", BudgetRules.RamGate("i", "n", 400, 300, 100).Value);
        Assert.Equal(0.15, Budgets.RamMarginFraction);
    }

    [Fact]
    public void Leak_slope_limit_is_two_mb_per_minute()
    {
        Assert.Equal(2, Budgets.LeakMbPerMinute);
        Assert.Equal(CheckStatus.Pass, BudgetRules.Leak("i", "n", 1.9).Status);
        Assert.Equal(CheckStatus.Fail, BudgetRules.Leak("i", "n", 2.1).Status);
    }

    // ---- timecode (F2/F3) ------------------------------------------------------------------------------------------

    /// <summary>The format the maintainer asked for: HH:MM:SS.FF, FF = 1-based frame within the second, nearest frame.</summary>
    private sealed class Reference : ITimecodeFormatter
    {
        public string Format(double seconds, double fps)
        {
            var f = (long)Math.Round(Math.Max(0, seconds) * fps);
            var perSecond = (long)Math.Round(fps);
            var whole = f / perSecond;
            return string.Create(CultureInfo.InvariantCulture, $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}.{f % perSecond + 1:00}");
        }
    }

    private sealed class OldStyle : ITimecodeFormatter
    {
        public string Format(double seconds, double fps) =>
            string.Create(CultureInfo.InvariantCulture, $"{(int)seconds / 60}:{seconds % 60:00.0}");
    }

    private sealed class ZeroBased : ITimecodeFormatter
    {
        private readonly Reference _r = new();
        public string Format(double seconds, double fps)
        {
            var s = _r.Format(seconds, fps);
            var ff = int.Parse(s[^2..], CultureInfo.InvariantCulture) - 1;
            return s[..^2] + ff.ToString("00", CultureInfo.InvariantCulture);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void The_reference_formatter_passes(double fps) => Assert.Empty(TimecodeRules.Check(new Reference(), fps));

    [Fact]
    public void The_maintainers_examples_hold_for_the_reference()
    {
        var r = new Reference();
        Assert.Equal("00:25:30.04", r.Format(1530 + 3.0 / 60, 60));
        Assert.Equal("01:40:00.01", r.Format(6000, 60));
        Assert.Equal("00:00:00.60", r.Format(59.0 / 60, 60));
    }

    [Fact]
    public void Variable_width_or_zero_based_frames_fail()
    {
        Assert.NotEmpty(TimecodeRules.Check(new OldStyle(), 60));
        var zero = TimecodeRules.Check(new ZeroBased(), 30);
        Assert.Contains(zero, p => p.Contains("frame", StringComparison.OrdinalIgnoreCase));
    }
}
