// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Diagnostics;
using SteamPoweredPlayer.Views.Player;

namespace SteamPoweredPlayer.Tests.Player;

public sealed class FrameTimecodeTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void Passes_the_realcheck_rules(double fps) => Assert.Empty(TimecodeRules.Check(FrameTimecode.Instance, fps));

    [Fact]
    public void The_maintainers_examples()
    {
        Assert.Equal("00:25:30.04", FrameTimecode.Position(1530 + 3.0 / 60, 60));
        Assert.Equal("01:40:00.00", FrameTimecode.Length(6000, 60));
        Assert.Equal("00:25:30.04 / 01:40:00.00", FrameTimecode.PositionOfLength(1530 + 3.0 / 60, 6000, 60));
    }

    [Theory]
    [InlineData(0, 30, "00:00:00.01")]
    [InlineData(0, 60, "00:00:00.01")]
    [InlineData(29.0 / 30, 30, "00:00:00.30")]
    [InlineData(59.0 / 60, 60, "00:00:00.60")]
    [InlineData(1, 60, "00:00:01.01")]
    [InlineData(3599 + 59.0 / 60, 60, "00:59:59.60")]
    [InlineData(3600, 60, "01:00:00.01")]
    public void Position_is_one_based_within_the_second(double seconds, double fps, string expected) =>
        Assert.Equal(expected, FrameTimecode.Position(seconds, fps));

    [Fact]
    public void The_head_of_a_clip_whose_first_frame_is_late_by_a_fraction_of_a_frame_is_frame_01()
    {
        Assert.Equal("00:00:00.01", FrameTimecode.Position(0.011, 60));      // a real clip: first frame 11 ms after the axis origin
        Assert.Equal("00:00:00.02", FrameTimecode.Position(0.011 + 1.0 / 60, 60));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(59.94)]
    public void Every_frame_step_moves_FF_by_one_and_wraps_to_01(double fps)
    {
        var perSecond = (int)Math.Ceiling(fps - 1e-6);
        string? previous = null;
        for (var k = 0; k < (int)(fps * 3); k++)
        {
            var pts = Math.Round(0.011 + 7 + k / fps, 3);        // ms-rounded pts, as Steam stores them
            var s = FrameTimecode.Position(pts, fps);
            Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{2}$", s);
            if (previous is not null)
            {
                var ff0 = int.Parse(previous[^2..]);
                var ff1 = int.Parse(s[^2..]);
                var sec0 = int.Parse(previous[6..8]);
                var sec1 = int.Parse(s[6..8]);
                if (sec1 == sec0) Assert.Equal(ff0 + 1, ff1);
                else
                {
                    Assert.Equal((sec0 + 1) % 60, sec1);
                    Assert.Equal(1, ff1);
                    Assert.InRange(ff0, perSecond - 1, perSecond);           // 59.94 fps: some seconds hold 59 frames
                }
            }
            previous = s;
        }
    }

    [Fact]
    public void At_59_94_fps_the_seconds_follow_the_clock_and_FF_stays_within_01_to_60()
    {
        const double fps = 60000.0 / 1001;
        Assert.Equal("01:40:00.01", FrameTimecode.Position(6000.02, fps));      // no drift: the seconds are wall-clock seconds
        Assert.Equal("01:39:59.60", FrameTimecode.Position(6000, fps));         // that frame began 6 ms before the second
        for (var k = 0; k < 600; k++)
        {
            var ff = int.Parse(FrameTimecode.Position(k / fps, fps)[^2..]);
            Assert.InRange(ff, 1, 60);
        }
    }

    [Theory]
    [InlineData(6.0, 60, "00:00:06.00")]
    [InlineData(6.0 - 1.0 / 60, 60, "00:00:05.59")]
    [InlineData(12.5, 30, "00:00:12.15")]
    [InlineData(0, 60, "00:00:00.00")]
    public void Length_counts_whole_frames(double seconds, double fps, string expected) =>
        Assert.Equal(expected, FrameTimecode.Length(seconds, fps));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-3)]
    [InlineData(double.PositiveInfinity)]
    public void Bad_input_is_the_head(double seconds) => Assert.Equal("00:00:00.01", FrameTimecode.Position(seconds, 60));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(5000)]
    public void An_unknown_rate_falls_back(double fps) => Assert.Equal(FrameTimecode.FallbackFps, FrameTimecode.Sanitize(fps));

    [Fact]
    public void The_width_is_fixed()
    {
        var widths = new[] { 0, 9.5, 59.99, 3599.9, 36000 }.Select(s => FrameTimecode.PositionOfLength(s, 36000, 60).Length).Distinct();
        Assert.Single(widths);
    }
}
