// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Views.Player;

/// <summary>
/// The visible window of the timeline (plan 2026-11-07 F4): which part of the recording the strip shows, in UI seconds. Zoom keeps the time
/// under the anchor (the pointer, or the playhead for the buttons) where it is on screen; scrolling and following the playhead keep the
/// window inside the recording. Pure (no XAML), so the maths is unit-tested.
/// </summary>
public sealed class TimelineView
{
    /// <summary>The narrowest window: a few seconds, so single frames are still a few pixels apart.</summary>
    public const double MinLengthSeconds = 2;

    /// <summary>Each zoom step (a wheel notch, a button press) shows this much less (or more).</summary>
    public const double StepFactor = 1.5;

    /// <summary>Following the playhead: when it leaves the window (or passes this fraction of it) the window pages forward…</summary>
    public const double FollowEdge = 0.95;

    /// <summary>…so that the playhead sits at this fraction of the new window.</summary>
    public const double FollowLead = 0.1;

    public double Duration { get; private set; }

    /// <summary>The first visible second.</summary>
    public double Start { get; private set; }

    /// <summary>How many seconds are visible (= <see cref="Duration"/> when not zoomed).</summary>
    public double Length { get; private set; }

    public double End => Start + Length;

    /// <summary>Duration / visible length: 1 = the whole recording.</summary>
    public double Zoom => Length > 0 ? Duration / Length : 1;

    public bool IsZoomed => Duration > 0 && Length < Duration - 1e-9;

    /// <summary>The recording got longer or shorter (a live session, a trim). Unzoomed stays unzoomed; a zoomed window keeps its length.</summary>
    public void SetDuration(double duration)
    {
        duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        var wasZoomed = IsZoomed;
        Duration = duration;
        Length = wasZoomed ? Math.Min(Length, duration) : duration;
        Clamp();
    }

    /// <summary>Back to the whole recording.</summary>
    public void Reset()
    {
        Start = 0;
        Length = Duration;
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/> (&gt; 1 in, &lt; 1 out) keeping <paramref name="anchorSeconds"/> at the same place on screen. Returns
    /// whether the window changed.
    /// </summary>
    public bool ZoomAt(double anchorSeconds, double factor)
    {
        if (Duration <= 0 || !(factor > 0) || !double.IsFinite(factor)) return false;
        var minLength = Math.Min(MinLengthSeconds, Duration);
        var length = Math.Clamp(Length / factor, minLength, Duration);
        if (Math.Abs(length - Length) < 1e-9) return false;
        anchorSeconds = Math.Clamp(double.IsFinite(anchorSeconds) ? anchorSeconds : Start, 0, Duration);
        var fraction = Length > 0 ? Math.Clamp((anchorSeconds - Start) / Length, 0, 1) : 0;
        Length = length;
        Start = anchorSeconds - fraction * length;
        Clamp();
        return true;
    }

    /// <summary>Moves the window by <paramref name="seconds"/> (clamped to the recording). Returns whether it moved.</summary>
    public bool ScrollBy(double seconds) => ScrollTo(Start + seconds);

    public bool ScrollTo(double start)
    {
        var before = Start;
        Start = double.IsFinite(start) ? start : Start;
        Clamp();
        return Math.Abs(before - Start) > 1e-9;
    }

    /// <summary>Keeps the playhead in view while it plays: pages when it leaves the window or nears its right edge. Returns whether it moved.</summary>
    public bool Follow(double position)
    {
        if (!IsZoomed || !double.IsFinite(position)) return false;
        if (position >= Start && position <= Start + Length * FollowEdge) return false;
        return ScrollTo(position - Length * FollowLead);
    }

    /// <summary>The x of <paramref name="seconds"/> on a strip <paramref name="width"/> pixels wide (outside the window: &lt; 0 or &gt; width).</summary>
    public double XOf(double seconds, double width) => Length <= 0 || width <= 0 ? 0 : (seconds - Start) / Length * width;

    /// <summary>The second under <paramref name="x"/>, clamped to the window.</summary>
    public double SecondsAt(double x, double width) =>
        width <= 0 || Length <= 0 ? Start : Start + Math.Clamp(x / width, 0, 1) * Length;

    public bool IsVisible(double seconds) => seconds >= Start && seconds <= End;

    private void Clamp()
    {
        if (Length <= 0 || Length > Duration) Length = Duration;
        Start = Math.Clamp(Start, 0, Math.Max(0, Duration - Length));
    }
}
