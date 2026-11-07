// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Playback;

/// <summary>What a timeline entry is, for the events filter and the marker icon (the app maps a kind to a glyph).</summary>
public enum MarkerKind { Kill, Death, MultiKill, Objective, Screenshot, Achievement, Phase, Other }

/// <summary>Pure helpers for the Player page's events list and next/previous event keys. No UI types.</summary>
public static class MarkerKinds
{
    /// <summary>
    /// Classifies by Steam's <c>icon</c> (e.g. <c>cs2_gun_kill</c>, <c>cs2_death</c>, <c>cs2_multi_kill</c>, <c>cs2_bomb_plant</c>,
    /// <c>steam_screenshot</c>), falling back to the raw type text, then to the entry type.
    /// </summary>
    public static MarkerKind Classify(TimelineEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e.Type)
        {
            case TimelineEntryType.Screenshot: return MarkerKind.Screenshot;
            case TimelineEntryType.Achievement: return MarkerKind.Achievement;
            case TimelineEntryType.Phase:
            case TimelineEntryType.GameMode:
            case TimelineEntryType.StateDescription: return MarkerKind.Phase;
        }

        var id = ((e.Icon ?? "") + " " + e.RawType).ToLowerInvariant();
        if (id.Contains("screenshot", StringComparison.Ordinal)) return MarkerKind.Screenshot;
        if (id.Contains("achievement", StringComparison.Ordinal)) return MarkerKind.Achievement;
        if (id.Contains("multi_kill", StringComparison.Ordinal) || id.Contains("double_kill", StringComparison.Ordinal)
            || id.Contains("triple_kill", StringComparison.Ordinal) || id.Contains("streak", StringComparison.Ordinal))
            return MarkerKind.MultiKill;
        if (id.Contains("death", StringComparison.Ordinal) || id.Contains("died", StringComparison.Ordinal)) return MarkerKind.Death;
        if (id.Contains("kill", StringComparison.Ordinal)) return e.DurationMs > 0 ? MarkerKind.MultiKill : MarkerKind.Kill;
        if (id.Contains("bomb", StringComparison.Ordinal) || id.Contains("defus", StringComparison.Ordinal)
            || id.Contains("plant", StringComparison.Ordinal) || id.Contains("objective", StringComparison.Ordinal)
            || id.Contains("capture", StringComparison.Ordinal))
            return MarkerKind.Objective;
        return MarkerKind.Other;
    }

    /// <summary>Phases, game modes and state texts are bands in Steam's player, not point events: no icon in the markers lane.</summary>
    public static bool ShowOnLane(MarkerKind kind) => kind != MarkerKind.Phase;
}

/// <summary>"Jump to marker" and next / previous event (N / P). All times are on the same axis as the markers (media time).</summary>
public static class MarkerNavigation
{
    /// <summary>A jump lands this far before the marker (plan §3 item 4: 2–3 s early) so the event is seen coming.</summary>
    public const double DefaultLeadSeconds = 2.5;

    /// <summary>Where a jump to <paramref name="marker"/> lands: <see cref="Marker.StartSeconds"/> minus the lead, never below zero.</summary>
    public static double LandingSeconds(Marker marker, double leadSeconds = DefaultLeadSeconds)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return Math.Max(0, marker.StartSeconds - leadSeconds);
    }

    /// <summary>
    /// The first marker whose landing point is ahead of <paramref name="positionSeconds"/> by more than <paramref name="epsilon"/>
    /// (so pressing N repeatedly walks the list: after landing on one, the next press goes to the following). Null at the end.
    /// <paramref name="markers"/> must be sorted by start (as <see cref="Markers.Map"/> returns them).
    /// </summary>
    public static Marker? Next(IReadOnlyList<Marker> markers, double positionSeconds, double leadSeconds = DefaultLeadSeconds, double epsilon = 0.25)
    {
        ArgumentNullException.ThrowIfNull(markers);
        foreach (var m in markers)
            if (LandingSeconds(m, leadSeconds) > positionSeconds + epsilon) return m;
        return null;
    }

    /// <summary>
    /// The last marker whose landing point is behind <paramref name="positionSeconds"/> by more than <paramref name="minGapSeconds"/>:
    /// right after a jump (still before the event) it is the previous event, later in the event it is that event again. Null at the start.
    /// </summary>
    public static Marker? Previous(IReadOnlyList<Marker> markers, double positionSeconds, double leadSeconds = DefaultLeadSeconds, double minGapSeconds = 1.0)
    {
        ArgumentNullException.ThrowIfNull(markers);
        Marker? found = null;
        foreach (var m in markers)
        {
            if (LandingSeconds(m, leadSeconds) < positionSeconds - minGapSeconds) found = m;
            else break;
        }
        return found;
    }
}
