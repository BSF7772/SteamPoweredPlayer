// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Tests.Fakes;

namespace SteamPoweredPlayer.Tests.Playback;

/// <summary>P2: a frame back lands on the previous frame's real pts, not on "position − 1/fps" (Steam's pts are irregular).</summary>
public sealed class PlaybackSessionFrameBackTests
{
    // 59.094 average fps like the real clip, with jittered pts: the nominal step misses frame boundaries.
    private static double[] IrregularPts(int count, double origin = 30)
    {
        var rnd = new Random(7);
        var pts = new double[count];
        var t = origin;
        for (var i = 0; i < count; i++) { pts[i] = Math.Round(t, 3); t += 1 / 59.094 + (rnd.NextDouble() - 0.5) * 0.008; }
        return pts;
    }

    private static async Task<(PlaybackSession S, FakeMpvPlayer P, double[] Pts)> OpenAtAsync(int frame)
    {
        var pts = IrregularPts(400);
        var p = new FakeMpvPlayer { FramePts = pts };
        var s = new PlaybackSession(p) { FrameProbeTimeout = TimeSpan.FromMilliseconds(200) };
        await s.OpenAsync(PlaybackSessionTests.Media(false, (11, 40)), pts[frame] - 0.0004, play: false);
        Assert.Equal(pts[frame], p.TimePos);
        return (s, p, pts);
    }

    [Fact]
    public async Task Ten_frame_backs_each_land_on_the_previous_frames_pts()
    {
        var (s, p, pts) = await OpenAtAsync(100);
        for (var k = 1; k <= 10; k++)
        {
            await s.StepBackAsync(1 / 59.094);
            Assert.Equal(pts[100 - k], p.TimePos);          // exactly one frame back, whatever the jitter
            Assert.Equal(pts[100 - k], s.Position);          // and the position (timecode) is that frame's pts
            Assert.True(p.Paused);
        }
    }

    [Fact]
    public async Task Rapid_frame_backs_each_count_once()
    {
        var (s, p, pts) = await OpenAtAsync(100);
        var a = s.StepBackAsync(1 / 59.094);
        var b = s.StepBackAsync(1 / 59.094);
        var c = s.StepBackAsync(1 / 59.094);
        await Task.WhenAll(a, b, c);
        Assert.Equal(pts[97], p.TimePos);
    }

    [Fact]
    public async Task A_forward_frame_step_between_backs_is_respected()
    {
        var (s, p, pts) = await OpenAtAsync(100);
        await s.StepBackAsync(1 / 59.094);
        await s.StepBackAsync(1 / 59.094);                  // frame 98
        s.FrameStep();                                       // frame 99
        Assert.Equal(pts[99], p.TimePos);
        await s.StepBackAsync(1 / 59.094);
        Assert.Equal(pts[98], p.TimePos);
    }

    [Fact]
    public async Task Back_after_a_seek_starts_from_the_sought_frame()
    {
        var (s, p, pts) = await OpenAtAsync(100);
        await s.SeekAsync(pts[200] - 0.0003);
        await s.StepBackAsync(1 / 59.094);
        Assert.Equal(pts[199], p.TimePos);
    }

    [Fact]
    public async Task Without_readable_frame_times_a_back_falls_back_to_one_nominal_frame()
    {
        var p = new FakeMpvPlayer();
        var s = new PlaybackSession(p) { FrameProbeTimeout = TimeSpan.FromMilliseconds(50) };
        await s.OpenAsync(PlaybackSessionTests.Media(false, (1, 300)), 60, play: false);
        await s.StepBackAsync(1.0 / 60);
        Assert.Equal(60 - 1.0 / 60, p.Loads[^1].Start, 9);
    }
}
