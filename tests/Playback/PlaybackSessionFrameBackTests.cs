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
    public async Task One_slow_step_does_not_disable_exact_backs()
    {
        var (s, p, pts) = await OpenAtAsync(100);
        s.FrameProbeTimeout = TimeSpan.FromMilliseconds(30);
        p.SilentSteps = 1;                                   // a frame step that reports nothing in time (a slow decode)
        await s.StepBackAsync(1 / 59.094);
        await s.SeekAsync(pts[200] - 0.0003);
        await s.StepBackAsync(1 / 59.094);
        Assert.Equal(pts[199], p.TimePos);
    }

    [Fact]
    public async Task Pausing_for_a_frame_step_leaves_a_paused_player_alone()
    {
        // Setting mpv's pause again cancels a frame step in flight, and the probe then times out: every press did this on the real player.
        var (s, p, _) = await OpenAtAsync(100);
        s.SetPause(true);
        var before = p.PauseCalls;
        s.PauseForFrameStep();
        Assert.Equal(before, p.PauseCalls);
        s.SetPause(false);
        s.PauseForFrameStep();
        Assert.True(p.Paused);
    }

    [Fact]
    public async Task Exact_backs_come_back_after_repeated_timeouts()
    {
        var (s, p, pts) = await OpenAtAsync(100);
        s.FrameProbeTimeout = TimeSpan.FromMilliseconds(20);
        p.SilentSteps = 3;                                   // three slow probes reach the limit
        for (var i = 0; i < 3; i++) await s.StepBackAsync(1 / 59.094);
        for (var i = 0; i < 8; i++) await s.StepBackAsync(1 / 59.094);      // nominal for a while, then it tries again
        s.FrameProbeTimeout = TimeSpan.FromMilliseconds(500);
        await s.SeekAsync(pts[200] - 0.0003);
        await s.StepBackAsync(1 / 59.094);
        Assert.Equal(pts[199], p.TimePos);
    }

    [Fact]
    public async Task A_nominal_back_that_lands_on_the_same_frame_goes_further()
    {
        // A chunk's first frame over a frame off the grid: one nominal frame back still shows that frame.
        var pts = new[] { 30.000, 30.017, 30.100, 30.117 };
        var p = new FakeMpvPlayer { FramePts = pts };
        var s = new PlaybackSession(p) { FrameProbeTimeout = TimeSpan.FromMilliseconds(20) };
        await s.OpenAsync(PlaybackSessionTests.Media(false, (11, 40)), 30.0995, play: false);
        Assert.Equal(30.100, p.TimePos);
        p.SilentSteps = 100;                                 // force the nominal path
        await s.StepBackAsync(1.0 / 60);
        Assert.True(p.TimePos < 30.100, $"still on {p.TimePos}");
    }

    [Fact]
    public async Task A_press_during_a_queued_back_counts_from_where_the_queue_is_going()
    {
        // Found on the real app, 200 frames moved in 210 presses: a finishing load put the position back on its own frame, so the next press forgot the one queued.
        const double frame = 1.0 / 60;
        var p = new FakeMpvPlayer();
        var s = new PlaybackSession(p) { FrameProbeTimeout = TimeSpan.FromMilliseconds(10) };
        await s.OpenAsync(PlaybackSessionTests.Media(false, (1, 300)), 60, play: false);

        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        p.Gate = first;
        var loads = p.Loads.Count;
        var a = s.StepBackAsync(frame);                      // press 1 is loading
        await WaitForLoads(p, loads + 1);
        var b = s.StepBackAsync(frame);                      // press 2 waits behind it
        loads = p.Loads.Count;
        p.Gate = second;
        first.SetResult();                                   // press 1 finishes, press 2 starts loading
        await WaitForLoads(p, loads + 1);
        var c = s.StepBackAsync(frame);                      // press 3 arrives while nothing is queued
        p.Gate = null;
        second.SetResult();
        await Task.WhenAll(a, b, c);
        Assert.Equal(60 - 3 * frame, p.Loads[^1].Start, 6);
    }

    private static async Task WaitForLoads(FakeMpvPlayer p, int count)
    {
        for (var i = 0; i < 400 && p.Loads.Count < count; i++) await Task.Delay(5);
        Assert.True(p.Loads.Count >= count, "the load never came");
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
