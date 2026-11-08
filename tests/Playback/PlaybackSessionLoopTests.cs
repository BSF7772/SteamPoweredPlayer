// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Tests.Fakes;

namespace SteamPoweredPlayer.Tests.Playback;

/// <summary>P1: the A-B loop is the session's (a seek-by-reload to A when the position reaches B), not mpv's internal ab-loop.</summary>
public sealed class PlaybackSessionLoopTests
{
    private static async Task<(PlaybackSession S, FakeMpvPlayer P)> Open(double start = 0)
    {
        var p = new FakeMpvPlayer();
        var s = new PlaybackSession(p);
        await s.OpenAsync(PlaybackSessionTests.Media(false, (1, 300)), start);
        return (s, p);
    }

    [Fact]
    public async Task Reaching_B_reloads_at_A_keeping_play()
    {
        var (s, p) = await Open(10);
        s.SetLoop(12, 15);
        p.Tick(14.95);
        Assert.Equal(1, p.LoadCount);
        p.Tick(15.0);
        await PlaybackSessionTests.WaitUntil(() => p.LoadCount == 2);
        Assert.Equal(12, p.Loads[1].Start);
        Assert.True(p.Loads[1].Play);
        Assert.Equal(p.Loads[0].Edl, p.Loads[1].Edl);        // same window: the jump is as cheap as a seek
    }

    [Fact]
    public async Task One_jump_per_burst_of_ticks_past_B()
    {
        var (s, p) = await Open(10);
        p.Gate = new TaskCompletionSource();
        s.SetLoop(12, 15);
        p.Tick(15.0);
        p.Tick(15.02);
        p.Tick(15.04);
        p.Gate.SetResult();
        await PlaybackSessionTests.WaitUntil(() => !s.IsReloading);
        Assert.Equal(2, p.LoadCount);
    }

    [Fact]
    public async Task Loops_again_after_each_lap()
    {
        var (s, p) = await Open(12);
        s.SetLoop(12, 15);
        for (var lap = 1; lap <= 3; lap++)
        {
            p.Tick(15.0);
            await PlaybackSessionTests.WaitUntil(() => p.LoadCount == 1 + lap && !s.IsReloading);
            Assert.Equal(12, p.Loads[lap].Start);
        }
    }

    [Fact]
    public async Task Paused_loop_keeps_the_pause_intent()
    {
        var (s, p) = await Open(10);
        s.SetPause(true);
        s.SetLoop(12, 15);
        p.Tick(15.0);
        await PlaybackSessionTests.WaitUntil(() => p.LoadCount == 2);
        Assert.False(p.Loads[1].Play);
    }

    [Fact]
    public async Task Cleared_or_incomplete_loop_does_not_jump()
    {
        var (s, p) = await Open(10);
        s.SetLoop(12, null);
        p.Tick(40);
        s.SetLoop(12, 15);
        s.SetLoop(null, null);
        p.Tick(40);
        s.SetLoop(15, 12);                                    // B before A is no loop
        p.Tick(40);
        await Task.Delay(50);
        Assert.Equal(1, p.LoadCount);
    }

    [Fact]
    public async Task Position_before_A_is_left_alone_until_it_reaches_B()
    {
        var (s, p) = await Open(0);
        s.SetLoop(12, 15);
        p.Tick(3);                                             // the user may play up to A first
        await Task.Delay(30);
        Assert.Equal(1, p.LoadCount);
    }

    [Fact]
    public async Task EndOfFile_inside_a_loop_jumps_to_A()
    {
        var (s, p) = await Open(10);
        s.SetLoop(12, 5000);                                   // B past what mpv will ever report
        p.Eof(299);
        await PlaybackSessionTests.WaitUntil(() => p.LoadCount == 2);
        Assert.Equal(12, p.Loads[1].Start);
    }

    [Fact]
    public async Task Mpv_own_loop_is_switched_off()
    {
        var (s, p) = await Open(10);
        s.SetLoop(12, 15);
        Assert.Equal((null, null), p.AbLoop);
    }
}
