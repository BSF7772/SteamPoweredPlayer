// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Views.Player;

namespace SteamPoweredPlayer.Tests.Player;

public sealed class TimelineViewTests
{
    private static TimelineView View(double duration)
    {
        var v = new TimelineView();
        v.SetDuration(duration);
        return v;
    }

    [Fact]
    public void Starts_showing_the_whole_recording()
    {
        var v = View(600);
        Assert.False(v.IsZoomed);
        Assert.Equal(0, v.Start);
        Assert.Equal(600, v.Length);
        Assert.Equal(1, v.Zoom);
        Assert.Equal(500, v.XOf(300, 1000), 6);
        Assert.Equal(300, v.SecondsAt(500, 1000), 6);
    }

    [Fact]
    public void Zoom_keeps_the_anchor_under_the_cursor()
    {
        var v = View(600);
        var xBefore = v.XOf(150, 1000);
        Assert.True(v.ZoomAt(150, 4));
        Assert.Equal(150, v.Length, 6);
        Assert.Equal(xBefore, v.XOf(150, 1000), 6);
        Assert.True(v.IsZoomed);

        Assert.Equal(112.5, v.Start, 6);

        var x170 = v.XOf(170, 1000);
        Assert.True(v.ZoomAt(170, 2));
        Assert.Equal(75, v.Length, 6);
        Assert.Equal(x170, v.XOf(170, 1000), 6);
    }

    [Fact]
    public void Zoom_near_an_edge_stays_inside_the_recording()
    {
        var v = View(600);
        var x595 = v.XOf(595, 1000);
        v.ZoomAt(595, 10);
        Assert.Equal(60, v.Length, 6);
        Assert.Equal(x595, v.XOf(595, 1000), 6);
        Assert.InRange(v.End, 595, 600);
        v.ZoomAt(1, 2);
        Assert.Equal(30, v.Length, 6);
        Assert.InRange(v.Start, 0, 570);
    }

    [Fact]
    public void Zoom_is_bounded()
    {
        var v = View(600);
        for (var i = 0; i < 50; i++) v.ZoomAt(300, TimelineView.StepFactor);
        Assert.Equal(TimelineView.MinLengthSeconds, v.Length, 6);
        Assert.False(v.ZoomAt(300, 2));                     // already at the limit
        for (var i = 0; i < 50; i++) v.ZoomAt(300, 1 / TimelineView.StepFactor);
        Assert.False(v.IsZoomed);
        Assert.Equal(0, v.Start);
    }

    [Fact]
    public void A_recording_shorter_than_the_minimum_cannot_zoom()
    {
        var v = View(1.5);
        Assert.False(v.ZoomAt(1, 4));
        Assert.Equal(1.5, v.Length);
    }

    [Fact]
    public void Scroll_is_clamped()
    {
        var v = View(600);
        v.ZoomAt(0, 6);                                      // 100 s window at the head
        Assert.True(v.ScrollBy(250));
        Assert.Equal(250, v.Start, 6);
        v.ScrollBy(1000);
        Assert.Equal(500, v.Start, 6);
        v.ScrollBy(-9999);
        Assert.Equal(0, v.Start, 6);
        Assert.False(v.ScrollBy(-1));
    }

    [Fact]
    public void Follow_pages_when_the_playhead_nears_the_edge_or_leaves()
    {
        var v = View(600);
        v.ZoomAt(0, 6);                                      // 0..100
        Assert.False(v.Follow(50));
        Assert.True(v.Follow(96));                           // past 95 %
        Assert.Equal(96 - 10, v.Start, 6);
        Assert.True(v.Follow(10));                           // jumped back before the window
        Assert.Equal(0, v.Start, 6);
        Assert.True(v.Follow(599));
        Assert.Equal(500, v.Start, 6);                       // clamped at the end
        Assert.True(v.IsVisible(599));
    }

    [Fact]
    public void Follow_does_nothing_unzoomed()
    {
        var v = View(600);
        Assert.False(v.Follow(599));
    }

    [Fact]
    public void A_growing_live_session_keeps_the_zoomed_length_and_an_unzoomed_view_grows()
    {
        var full = View(600);
        full.SetDuration(603);
        Assert.Equal(603, full.Length);

        var zoomed = View(600);
        zoomed.ZoomAt(300, 6);
        var length = zoomed.Length;
        zoomed.SetDuration(603);
        Assert.Equal(length, zoomed.Length, 6);
        zoomed.SetDuration(50);                              // trimmed below the window
        Assert.Equal(50, zoomed.Length, 6);
        Assert.Equal(0, zoomed.Start);
    }

    [Fact]
    public void Reset_shows_everything()
    {
        var v = View(600);
        v.ZoomAt(200, 8);
        v.Reset();
        Assert.False(v.IsZoomed);
        Assert.Equal(0, v.Start);
    }

    [Fact]
    public void SecondsAt_is_clamped_to_the_window()
    {
        var v = View(600);
        v.ZoomAt(0, 6);
        v.ScrollTo(100);
        Assert.Equal(100, v.SecondsAt(-50, 1000), 6);
        Assert.Equal(200, v.SecondsAt(5000, 1000), 6);
        Assert.True(v.XOf(50, 1000) < 0);
    }
}
