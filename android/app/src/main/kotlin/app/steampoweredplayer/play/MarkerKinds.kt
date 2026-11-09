// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import app.steampoweredplayer.data.Marker
import app.steampoweredplayer.data.TimelineEntry
import app.steampoweredplayer.data.TimelineEntryType
import kotlin.math.max

enum class MarkerKind { Kill, Death, MultiKill, Objective, Screenshot, Achievement, Phase, Other }

/** As in the desktop's `MarkerKinds`. Pure. */
object MarkerKinds {
    fun classify(e: TimelineEntry): MarkerKind {
        when (e.type) {
            TimelineEntryType.Screenshot -> return MarkerKind.Screenshot
            TimelineEntryType.Achievement -> return MarkerKind.Achievement
            TimelineEntryType.Phase, TimelineEntryType.GameMode, TimelineEntryType.StateDescription -> return MarkerKind.Phase
            else -> Unit
        }
        val id = ((e.icon ?: "") + " " + e.rawType).lowercase()
        return when {
            "screenshot" in id -> MarkerKind.Screenshot
            "achievement" in id -> MarkerKind.Achievement
            "multi_kill" in id || "double_kill" in id || "triple_kill" in id || "streak" in id -> MarkerKind.MultiKill
            "death" in id || "died" in id -> MarkerKind.Death
            "kill" in id -> if (e.durationMs > 0) MarkerKind.MultiKill else MarkerKind.Kill
            "bomb" in id || "defus" in id || "plant" in id || "objective" in id || "capture" in id -> MarkerKind.Objective
            else -> MarkerKind.Other
        }
    }

    /** Phases and game modes are bands in Steam's player, not point events. */
    fun showOnLane(kind: MarkerKind) = kind != MarkerKind.Phase

    fun emoji(kind: MarkerKind) = when (kind) {
        MarkerKind.Kill -> "🎯"
        MarkerKind.Death -> "💀"
        MarkerKind.MultiKill -> "🔥"
        MarkerKind.Objective -> "🏁"
        MarkerKind.Screenshot -> "📷"
        MarkerKind.Achievement -> "🏆"
        MarkerKind.Phase -> "🔖"
        MarkerKind.Other -> "📍"
    }
}

/** Next and previous event, as in the desktop's `MarkerNavigation`. Markers must be sorted by start. Pure. */
object MarkerNavigation {
    const val LEAD_SECONDS = 2.5

    fun landing(marker: Marker, lead: Double = LEAD_SECONDS) = max(0.0, marker.startSeconds - lead)

    fun next(markers: List<Marker>, position: Double, lead: Double = LEAD_SECONDS, epsilon: Double = 0.25): Marker? =
        markers.firstOrNull { landing(it, lead) > position + epsilon }

    fun previous(markers: List<Marker>, position: Double, lead: Double = LEAD_SECONDS, minGap: Double = 1.0): Marker? =
        markers.takeWhile { landing(it, lead) < position - minGap }.lastOrNull()

    /** Where "now" goes in a list of markers sorted by time: before the first one that starts after [position]. */
    fun nowIndex(starts: List<Double>, position: Double): Int {
        val i = starts.indexOfFirst { it > position }
        return if (i < 0) starts.size else i
    }

    /** The slice of a long list to show, at most [max] items, centred on [now] so the "you are here" line is always in it. */
    fun windowAround(count: Int, now: Int, max: Int): IntRange {
        if (count <= max) return 0 until count
        val from = (now - max / 2).coerceIn(0, count - max)
        return from until from + max
    }
}
