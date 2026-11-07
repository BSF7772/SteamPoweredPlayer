// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Tests.Fakes;

namespace SteamPoweredPlayer.Tests.Playback;

public sealed class PlaybackSessionTests
{
    internal static SessionMedia Media(bool live = false, params (int First, int Last)[] runs) =>
        new("C:/rec/bg_730_20250101_120000", [.. runs.Select(r => new SegmentRun(r.First, r.Last))], 3.0, live, true);

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > end) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }

    private static (PlaybackSession S, FakeMpvPlayer P) Create(int windowCount = EdlWindow.DefaultCount)
    {
        var p = new FakeMpvPlayer();
        return (new PlaybackSession(p, windowCount: windowCount), p);
    }

    [Fact]
    public async Task Open_loads_the_head_playing_with_a_lead_edl()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (11, 40)));
        var l = Assert.Single(p.Loads);
        Assert.Equal(30.0, l.Start);                 // chunk 11 starts at 30 s on the tfdt axis
        Assert.True(l.Play);
        Assert.Contains("chunk-stream0-00011.m4s", l.Edl);
        Assert.Equal(11, s.Window[0]);
    }

    [Fact]
    public async Task Seek_inside_the_window_reloads_with_the_same_edl()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 60);      // window around chunk 21: 6..65
        var window = s.Window;
        await s.SeekAsync(100);                             // chunk 34: inside, far from the end
        Assert.Equal(2, p.LoadCount);
        Assert.Equal(100, p.Loads[1].Start);
        Assert.Equal(p.Loads[0].Edl, p.Loads[1].Edl);
        Assert.Same(window, s.Window);
    }

    [Fact]
    public async Task Seek_outside_the_window_rebuilds_it_around_the_target()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 0);
        await s.SeekAsync(600);                             // chunk 201
        Assert.NotEqual(p.Loads[0].Edl, p.Loads[1].Edl);
        Assert.Contains(201, s.Window);
        Assert.Equal(600, p.Loads[1].Start);
    }

    [Fact]
    public async Task Seek_near_the_window_end_rebuilds_it()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 0);       // window 1..60
        await s.SeekAsync(160);                             // chunk 54: 6 chunks from the end
        Assert.NotEqual(p.Loads[0].Edl, p.Loads[1].Edl);
        Assert.True(s.Window[^1] > 60);
    }

    [Fact]
    public async Task Pause_intent_is_kept_across_reloads_and_unpausing_plays_again()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 0);
        s.SetPause(true);
        await s.SeekAsync(100);
        Assert.False(p.Loads[1].Play);
        s.SetPause(false);
        await s.SeekAsync(200);
        Assert.True(p.Loads[2].Play);
    }

    [Fact]
    public async Task Open_paused_loads_paused()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 30)), play: false);
        Assert.False(p.Loads[0].Play);
        Assert.True(s.UserPaused);
    }

    [Fact]
    public async Task Seeks_during_a_reload_coalesce_to_the_latest()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 0);
        p.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var t1 = s.SeekAsync(100);
        await WaitUntil(() => p.LoadCount == 2);            // the first seek is in flight
        var t2 = s.SeekAsync(200);
        var t3 = s.SeekAsync(300);
        Assert.Equal(300, s.Position);                      // the UI sees the latest target at once
        var gate = p.Gate;
        p.Gate = null;
        gate.SetResult();
        await Task.WhenAll(t1, t2, t3);
        Assert.Equal(3, p.LoadCount);                       // open, 100, 300: the 200 was dropped
        Assert.Equal(300, p.Loads[2].Start);
        Assert.False(s.IsReloading);
    }

    [Fact]
    public async Task Ticks_during_a_reload_do_not_move_the_position()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 0);
        p.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = s.SeekAsync(400);
        await WaitUntil(() => p.LoadCount == 2);
        p.Tick(5);                                          // the old file still reports time
        Assert.Equal(400, s.Position);
        var g = p.Gate; p.Gate = null; g.SetResult();
        await t;
    }

    [Fact]
    public void Snap_handles_head_gap_and_end()
    {
        var m = Media(false, (11, 20), (31, 40));           // head 30 s, gap [60, 90), end 120 s
        Assert.Equal(30, PlaybackSession.Snap(m, 0));
        Assert.Equal(30, PlaybackSession.Snap(m, double.NaN));
        Assert.Equal(45, PlaybackSession.Snap(m, 45));
        Assert.Equal(90, PlaybackSession.Snap(m, 70));
        Assert.Equal(90, PlaybackSession.Snap(m, 60));
        Assert.Equal(120 - PlaybackSession.EndMarginSeconds, PlaybackSession.Snap(m, 999));
    }

    [Fact]
    public void Snap_keeps_the_last_fraction_of_a_second_before_a_gap_out_of_reach()
    {
        // found by --selftest: an hr-seek into the tail of the part that is stretched over a gap lands in the next run
        var m = Media(false, (11, 20), (31, 40));           // the gap starts at 60 s
        var margin = PlaybackSession.GapTailMarginSeconds;
        Assert.Equal(60 - margin, PlaybackSession.Snap(m, 59.99), 9);
        Assert.Equal(60 - margin, PlaybackSession.Snap(m, 60 - margin), 9);
        Assert.Equal(59.0, PlaybackSession.Snap(m, 59.0));
        Assert.Equal(119.0, PlaybackSession.Snap(m, 119.0));              // no gap after the last run: its tail is fine
        Assert.Equal(119.99, PlaybackSession.Snap(m, 119.99), 9);
    }

    [Fact]
    public async Task Ui_time_is_zero_based_and_duration_comes_from_the_index()
    {
        var (s, _) = Create();
        await s.OpenAsync(Media(false, (11, 20), (31, 40)), 45);     // 20 chunks, span 30..120
        Assert.Equal(45, s.Position);
        Assert.Equal(15, s.UiPosition);
        Assert.Equal(90, s.UiDuration);                             // span including the 30 s gap
        Assert.Equal(60, s.PlayableSeconds);
        Assert.Equal(45, s.UiToMedia(15));
        Assert.Equal(30, s.MediaStartSeconds);
        await s.SeekUiAsync(0);
        Assert.Equal(30, s.Position);
    }

    [Fact]
    public async Task Playing_near_the_window_end_reloads_by_policy()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 0);       // window 1..60 (ends at 180 s)
        p.Tick(1);
        Assert.Equal(1, p.LoadCount);
        p.Tick(150);                                        // chunk 51: within 10 of the end, 35 more chunks reachable
        await WaitUntil(() => p.LoadCount == 2 && !s.IsReloading);
        Assert.Equal(150, p.Loads[1].Start);
        Assert.True(p.Loads[1].Play);
        Assert.True(s.Window[^1] > 60);
    }

    [Fact]
    public async Task Periodic_reload_after_enough_sequential_media_seconds()
    {
        var (s, p) = Create(windowCount: 300);               // the window is the whole session: only the periodic rule can fire
        await s.OpenAsync(Media(false, (1, 300)), 0);
        for (var t = 1; t < 600; t++) p.Tick(t);
        Assert.Equal(1, p.LoadCount);
        p.Tick(600);
        await WaitUntil(() => p.LoadCount == 2 && !s.IsReloading);
        Assert.Equal(600, p.Loads[1].Start);
        for (var t = 601; t < 700; t++) p.Tick(t);          // the counter restarted
        Assert.Equal(2, p.LoadCount);
    }

    [Fact]
    public async Task A_growing_session_near_its_head_does_not_reload_on_every_new_chunk()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(true, (1, 100)), 270);       // window 41..100, at the session end
        s.ReplaceMedia(Media(true, (1, 105)));
        p.Tick(267);                                        // chunk 90: 10 from the window end, but only 5 more chunks exist
        await Task.Delay(50);
        Assert.Equal(1, p.LoadCount);
        s.ReplaceMedia(Media(true, (1, 115)));
        p.Tick(267.1);
        await WaitUntil(() => p.LoadCount == 2 && !s.IsReloading);
        Assert.Equal(115, s.Window[^1]);
    }

    [Fact]
    public async Task A_stale_window_is_rebuilt_on_the_next_tick()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 300);      // chunk 101: window 86..145
        s.ReplaceMedia(Media(false, (90, 300)));             // Steam trimmed the window's first chunk
        p.Tick(300.5);
        await WaitUntil(() => p.LoadCount == 2 && !s.IsReloading);
        Assert.True(s.Window[0] >= 90);
    }

    [Fact]
    public async Task Eof_marks_the_end_and_raises_the_event_until_the_next_reload()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 10)), 0);
        double? at = null;
        s.ReachedEnd += x => at = x;
        p.Eof(30);
        Assert.Equal(30, at);
        Assert.True(s.AtEnd);
        await s.SeekAsync(3);
        Assert.False(s.AtEnd);
    }

    [Fact]
    public async Task Back_step_and_frame_step()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 300)), 60);
        await s.StepBackAsync(1.0 / 60);
        Assert.Equal(60 - 1.0 / 60, p.Loads[1].Start, 9);
        s.FrameStep();
        Assert.Equal(1, p.FrameSteps);
        Assert.True(s.UserPaused);
    }

    [Fact]
    public async Task Speed_is_clamped_to_the_supported_range()
    {
        var (s, p) = Create();
        await s.OpenAsync(Media(false, (1, 10)), 0);
        s.SetSpeed(10);
        Assert.Equal(4, p.Speed);
        s.SetSpeed(0.01);
        Assert.Equal(0.25, p.Speed);
    }

    [Fact]
    public void Prewarm_is_a_two_chunk_edl_at_the_head()
    {
        var (edl, start) = PlaybackSession.BuildPreWarm(Media(false, (11, 300)));
        Assert.Equal(30, start);
        Assert.Contains("chunk-stream0-00012.m4s", edl);
        Assert.DoesNotContain("chunk-stream0-00013.m4s", edl);
    }
}
