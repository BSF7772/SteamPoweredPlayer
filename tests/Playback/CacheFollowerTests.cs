// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Tests.Fakes;
using static SteamPoweredPlayer.Tests.Playback.PlaybackSessionTests;

namespace SteamPoweredPlayer.Tests.Playback;

public sealed class CacheFollowerTests
{
    // ---- pure decisions ---------------------------------------------------------------------------------------------

    [Fact]
    public void Trim_behind_the_playhead_changes_nothing()
    {
        var d = CacheFollower.DecideOnMediaChange(Media(true, (1, 100)), Media(true, (21, 110)), 120);
        Assert.Equal(FollowAction.None, d.Action);
        Assert.Equal(60, d.HeadRemovedSeconds);
    }

    [Fact]
    public void Trim_over_the_playhead_jumps_to_the_new_head()
    {
        var d = CacheFollower.DecideOnMediaChange(Media(true, (1, 100)), Media(true, (21, 110)), 30);
        Assert.Equal(FollowAction.JumpToHead, d.Action);
        Assert.Equal(60, d.TargetSeconds);
        Assert.Equal(30, d.LostAtPlayheadSeconds);
        Assert.Equal(60, d.HeadRemovedSeconds);
    }

    [Fact]
    public void Playhead_exactly_at_the_new_head_stays()
    {
        Assert.Equal(FollowAction.None, CacheFollower.DecideOnMediaChange(Media(true, (1, 100)), Media(true, (21, 100)), 60).Action);
    }

    [Fact]
    public void Everything_removed_is_reported_as_emptied()
    {
        var d = CacheFollower.DecideOnMediaChange(Media(true, (1, 100)), Media(true), 30);
        Assert.Equal(FollowAction.Emptied, d.Action);
    }

    [Fact]
    public void Growth_alone_changes_nothing()
    {
        Assert.Equal(FollowAction.None, CacheFollower.DecideOnMediaChange(Media(true, (1, 100)), Media(true, (1, 130)), 30).Action);
    }

    [Theory]
    [InlineData(true, 61, EndAction.Continue)]       // more chunks than the window
    [InlineData(true, 60, EndAction.WaitForGrowth)]  // live head
    [InlineData(false, 60, EndAction.Finished)]      // finished recording
    [InlineData(false, 80, EndAction.Continue)]
    public void End_decisions(bool live, int lastChunk, EndAction expected)
    {
        var window = Enumerable.Range(1, 60).ToList();
        Assert.Equal(expected, CacheFollower.DecideAtEnd(Media(live, (1, lastChunk)), window));
    }

    // ---- with the session and the fake player -----------------------------------------------------------------------

    private static async Task<(PlaybackSession S, FakeMpvPlayer P, CacheFollower F)> Open(SessionMediaArgs a)
    {
        var p = new FakeMpvPlayer();
        var s = new PlaybackSession(p);
        var f = new CacheFollower(s);
        await s.OpenAsync(a.Media, a.Start, a.Play);
        return (s, p, f);
    }

    private sealed record SessionMediaArgs(Core.Library.SessionMedia Media, double Start, bool Play = true);

    [Fact]
    public async Task Trim_while_playing_inside_the_window_does_not_reload()
    {
        var (s, p, f) = await Open(new(Media(true, (1, 200)), 300));      // playing at 300 s (chunk 101)
        var notices = new List<CacheTrim>();
        f.Trimmed += notices.Add;
        await f.OnMediaChangedAsync(Media(true, (20, 205)));
        Assert.Equal(1, p.LoadCount);
        Assert.Empty(notices);
        Assert.Equal(205, s.Media!.LastChunk);
    }

    [Fact]
    public async Task Trim_over_the_playhead_while_playing_jumps_to_the_head_and_keeps_playing()
    {
        var (s, p, f) = await Open(new(Media(true, (1, 200)), 30));        // chunk 11
        var notices = new List<CacheTrim>();
        f.Trimmed += notices.Add;
        await f.OnMediaChangedAsync(Media(true, (41, 205)));              // head now 120 s
        Assert.Equal(2, p.LoadCount);
        Assert.Equal(120, p.Loads[1].Start);
        Assert.True(p.Loads[1].Play);
        var n = Assert.Single(notices);
        Assert.Equal(90, n.RemovedSeconds);                               // "Steam removed 90 s" ahead of the playhead
        Assert.Equal(120, n.HeadRemovedSeconds);
        Assert.Equal(120, s.Position);
        Assert.Equal(0, s.UiPosition);
    }

    [Fact]
    public async Task Trim_over_the_playhead_while_paused_jumps_but_stays_paused()
    {
        var (s, p, f) = await Open(new(Media(true, (1, 200)), 30, Play: false));
        await f.OnMediaChangedAsync(Media(true, (41, 205)));
        Assert.Equal(2, p.LoadCount);
        Assert.False(p.Loads[1].Play);
        Assert.Equal(120, p.Loads[1].Start);
    }

    [Fact]
    public async Task Emptied_media_is_reported_and_nothing_is_loaded()
    {
        var (_, p, f) = await Open(new(Media(true, (1, 20)), 30));
        CacheTrim? n = null;
        f.Trimmed += x => n = x;
        await f.OnMediaChangedAsync(Media(true));
        Assert.True(n!.Emptied);
        Assert.Equal(1, p.LoadCount);
    }

    [Fact]
    public async Task Live_eof_waits_then_continues_when_the_session_grows()
    {
        var (s, p, f) = await Open(new(Media(true, (1, 20)), 54));         // window = all 20 chunks, ends at 60 s
        p.Eof(60);
        await Task.Delay(50);
        Assert.Equal(1, p.LoadCount);                                     // nothing new on disk: wait
        Assert.True(s.AtEnd);

        await f.OnMediaChangedAsync(Media(true, (1, 25)));
        Assert.Equal(2, p.LoadCount);
        Assert.Equal(60, p.Loads[1].Start);                               // continues where the old window ended
        Assert.True(p.Loads[1].Play);
        Assert.Equal(25, s.Window[^1]);
        Assert.False(s.AtEnd);
    }

    [Fact]
    public async Task Eof_with_chunks_already_known_continues_at_once()
    {
        var (s, p, _) = await Open(new(Media(true, (1, 20)), 54));
        s.ReplaceMedia(Media(true, (1, 30)));
        p.Eof(60);
        await WaitUntil(() => p.LoadCount == 2 && !s.IsReloading);
        Assert.Equal(60, p.Loads[1].Start);
        Assert.Equal(30, s.Window[^1]);
    }

    [Fact]
    public async Task Eof_of_a_finished_recording_stays_put()
    {
        var (s, p, _) = await Open(new(Media(false, (1, 20)), 54));
        p.Eof(60);
        await Task.Delay(50);
        Assert.Equal(1, p.LoadCount);
        Assert.True(s.AtEnd);
    }

    [Fact]
    public async Task Growth_after_a_live_eof_does_not_resume_a_user_pause()
    {
        var (s, p, f) = await Open(new(Media(true, (1, 20)), 54));
        p.Eof(60);
        s.SetPause(true);
        await f.OnMediaChangedAsync(Media(true, (1, 25)));
        Assert.Equal(1, p.LoadCount);
    }
}
