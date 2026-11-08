// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.time.Duration
import java.time.Instant

enum class MarkerAlignment { Exact, Approximate, None }

/** A timeline entry placed on the media axis, in seconds. A range has [endSeconds]; a point doesn't. */
data class Marker(val entry: TimelineEntry, val startSeconds: Double, val endSeconds: Double?, val alignment: MarkerAlignment) {
    val isRange: Boolean get() = endSeconds != null
    val isApproximate: Boolean get() = alignment == MarkerAlignment.Approximate
}

data class MarkerSet(val markers: List<Marker>, val alignment: MarkerAlignment, val diagnostics: List<String>)

enum class TrimReason { BeforeStart, AfterEnd, InGap }

data class TrimmedMarker(val marker: Marker, val reason: TrimReason)

/** Markers split by what the chunks cover. Nothing is dropped silently: uncovered ones are in [trimmedAway]. */
data class MarkerPlacement(val visible: List<Marker>, val trimmedAway: List<TrimmedMarker>)

/** Mapping timeline entries to media time, as in the desktop's `Markers` (docs/steam-recording-format.md, "Mapping to video time"). Pure. */
object Markers {
    /**
     * `entry.time - exactOffsetMs` when the exact offset is known; otherwise `entry.time - (bgStart - timelineStart)` (about ±1 s, flagged approximate).
     * The timeline start defaults to the file's `daterecorded`. Sorted by start, ties keeping file order.
     */
    fun map(timeline: TimelineFile, exactOffsetMs: Long?, bgStartUtc: Instant? = null, timelineStartUtc: Instant? = null): MarkerSet {
        val offsetSeconds: Double
        val alignment: MarkerAlignment
        val timelineStart = timelineStartUtc ?: timeline.dateRecordedUtc
        if (exactOffsetMs != null) {
            offsetSeconds = exactOffsetMs / 1000.0
            alignment = MarkerAlignment.Exact
        } else if (bgStartUtc != null && timelineStart != null) {
            offsetSeconds = Duration.between(timelineStart, bgStartUtc).toMillis() / 1000.0
            alignment = MarkerAlignment.Approximate
        } else {
            return MarkerSet(emptyList(), MarkerAlignment.None, listOf("no exact offset and no session/timeline start times: markers cannot be placed"))
        }
        val markers = timeline.entries.map { e ->
            val start = e.timeMs / 1000.0 - offsetSeconds
            Marker(e, start, if (e.durationMs > 0) start + e.durationMs / 1000.0 else null, alignment)
        }
        return MarkerSet(markers.sortedBy { it.startSeconds }, alignment, emptyList())
    }

    /** A point is covered inside a run (chunk N covers [(N−1)·seg, N·seg)); a range when it overlaps one, so a partly covered range stays visible. */
    fun place(markers: Iterable<Marker>, media: SessionMedia): MarkerPlacement {
        val runs = media.runs.map { maxOf(media.chunkStartSeconds(it.first), media.startSeconds) to minOf(media.chunkEndSeconds(it.last), media.endSeconds) }
        val visible = ArrayList<Marker>()
        val trimmed = ArrayList<TrimmedMarker>()
        for (m in markers) {
            val s = m.startSeconds
            val e = m.endSeconds ?: s
            val covered = runs.any { (start, end) -> if (m.isRange) s < end && e > start else s >= start && s < end }
            if (covered) {
                visible.add(m)
                continue
            }
            val reason = when {
                runs.isEmpty() || (if (m.isRange) e <= runs.first().first else s < runs.first().first) -> TrimReason.BeforeStart
                s >= runs.last().second -> TrimReason.AfterEnd
                else -> TrimReason.InGap
            }
            trimmed.add(TrimmedMarker(m, reason))
        }
        return MarkerPlacement(visible, trimmed)
    }
}
