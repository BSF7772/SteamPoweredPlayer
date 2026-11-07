// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Services;

/// <summary>Maps timeline entries to an emoji glyph and a label for the Cache strip and the Player's markers lane (plan §3, point 4).</summary>
public static class MarkerGlyphs
{
    public const string Generic = "•";

    /// <summary>Entries worth a spot on a strip: events, screenshots and achievements. Phases, game modes, state texts and errors are not.</summary>
    public static bool IsSpot(TimelineEntry e) =>
        e.Type is TimelineEntryType.Event or TimelineEntryType.Screenshot or TimelineEntryType.Achievement;

    public static string GlyphFor(TimelineEntry e)
    {
        if (e.Type == TimelineEntryType.Screenshot) return "📷";
        if (e.Type == TimelineEntryType.Achievement) return "🏆";
        var key = ((e.Icon ?? "") + " " + (e.RawType ?? "")).ToLowerInvariant();
        if (key.Contains("death") || key.Contains("died") || key.Contains("dead")) return "💀";
        if (key.Contains("kill") || key.Contains("frag") || key.Contains("headshot") || key.Contains("eliminat")) return "🔫";
        if (key.Contains("bomb") || key.Contains("explo") || key.Contains("grenade")) return "💣";
        if (key.Contains("trophy") || key.Contains("achiev") || key.Contains("win") || key.Contains("victory")) return "🏆";
        if (key.Contains("screenshot")) return "📷";
        return Generic;
    }

    public static string LabelFor(TimelineEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.Title)) return e.Title!.Trim();
        if (!string.IsNullOrWhiteSpace(e.AchievementName)) return e.AchievementName!;
        if (!string.IsNullOrWhiteSpace(e.Icon)) return e.Icon!.Replace('_', ' ');
        return e.Type.ToString();
    }

    public static MarkerSpot ToSpot(Marker m) => new(m.StartSeconds, GlyphFor(m.Entry), LabelFor(m.Entry));
}
