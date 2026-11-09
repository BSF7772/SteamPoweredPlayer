// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import app.steampoweredplayer.data.SessionMedia
import kotlin.math.max
import kotlin.math.min

/** A kept range on the media (tfdt) axis. */
data class ExportSpan(val start: Double, val end: Double) {
    val length: Double get() = end - start
}

/** A named instant on the media axis; becomes a chapter when it falls inside the exported spans. */
data class ExportChapter(val mediaSeconds: Double, val title: String)

/** A chapter on the output file's axis. */
data class OutputChapter(val start: Double, val end: Double, val title: String)

/**
 * The chunks an export reads for a range on the media axis, as in the desktop's `ExportPlan`. [start] and [end] stay inside the recording's playable part; a lossless
 * export reads from [snappedStart] because only a chunk's start is a keyframe, and its edit lists hide everything outside [start, end).
 */
data class ExportPlan(
    val firstChunk: Int,
    val lastChunk: Int,
    val start: Double,
    val end: Double,
    val snappedStart: Double,
    val snappedEnd: Double,
    val gapSeconds: Double,
) {
    companion object {
        /** Clamped to the chunks there are. Null when no complete chunk overlaps the range or the range is empty. */
        fun of(media: SessionMedia, start: Double, end: Double): ExportPlan? {
            if (!start.isFinite() || !end.isFinite()) return null
            val from = max(start, media.startSeconds)
            val to = min(end, media.endSeconds)
            if (to <= from) return null
            var first: Int? = null
            var last: Int? = null
            var count = 0
            for (chunk in media.chunks()) {
                if (media.chunkEndSeconds(chunk) <= from || media.chunkStartSeconds(chunk) >= to) continue
                if (first == null) first = chunk
                last = chunk
                count++
            }
            val a = first ?: return null
            val b = last ?: return null
            val snappedStart = media.chunkStartSeconds(a)
            val snappedEnd = media.chunkEndSeconds(b)
            return ExportPlan(a, b, max(from, snappedStart), min(to, snappedEnd), snappedStart, snappedEnd, (b - a + 1 - count) * media.segmentSeconds)
        }

        fun whole(media: SessionMedia): ExportPlan? = if (media.chunkCount > 0) of(media, media.startSeconds, media.endSeconds) else null

        /**
         * The audio chunks that hold this plan's sound: output time t plays source time t − [audioDelay], so a sync shift reads past the picture's chunks, and
         * Steam's audio chunks can run up to a second off the grid. One chunk of margin each side covers that.
         */
        fun audioChunks(media: SessionMedia, plan: ExportPlan, audioDelay: Double): Pair<Int, Int> {
            val margin = media.segmentSeconds
            val range = of(media, plan.start - audioDelay - margin, plan.end - audioDelay + margin)
            return if (range == null) plan.firstChunk to plan.lastChunk else range.firstChunk to range.lastChunk
        }
    }
}

object ExportSpans {
    /** Sorted, without empty or invalid spans, overlapping and touching spans merged. */
    fun normalize(spans: Iterable<ExportSpan>): List<ExportSpan> {
        val out = ArrayList<ExportSpan>()
        for (s in spans.filter { it.start.isFinite() && it.end.isFinite() && it.end > it.start }.sortedBy { it.start }) {
            if (out.isNotEmpty() && s.start <= out.last().end) {
                out[out.lastIndex] = out.last().copy(end = max(out.last().end, s.end))
            } else {
                out.add(s)
            }
        }
        return out
    }

    /** One plan per kept range, in order, with its exact times. Two plans may share a chunk. */
    fun plans(media: SessionMedia, spans: Iterable<ExportSpan>): List<ExportPlan> = normalize(spans).mapNotNull { ExportPlan.of(media, it.start, it.end) }

    /** ± seconds around each moment, clamped to the recording and merged: the spans of a highlight reel. */
    fun highlights(media: SessionMedia, moments: Iterable<Double>, before: Double = 5.0, after: Double = 3.0): List<ExportSpan> {
        val whole = ExportPlan.whole(media) ?: return emptyList()
        return normalize(moments.filter { it.isFinite() && it >= whole.start && it <= whole.end }.map { ExportSpan(max(whole.start, it - before), min(whole.end, it + after)) })
    }

    fun outputSeconds(plans: Iterable<ExportPlan>, speed: Double): Double = plans.sumOf { it.end - it.start } / max(0.01, speed)
}

object ExportChapters {
    /** Keeps the chapters inside a plan, maps them through the cuts and the speed, and ends each where the next starts. Two markers on one instant keep the first. */
    fun layout(plans: List<ExportPlan>, chapters: Iterable<ExportChapter>, speed: Double): List<OutputChapter> {
        val rate = max(0.01, speed)
        val total = ExportSpans.outputSeconds(plans, rate)
        val placed = ArrayList<Pair<Double, String>>()
        for (c in chapters.sortedBy { it.mediaSeconds }) {
            var before = 0.0
            for (p in plans) {
                if (c.mediaSeconds >= p.start && c.mediaSeconds < p.end) {
                    val at = (before + c.mediaSeconds - p.start) / rate
                    if (placed.isEmpty() || at - placed.last().first >= 0.001) placed.add(at to c.title)
                    break
                }
                before += p.end - p.start
            }
        }
        if (placed.isNotEmpty() && placed[0].first >= 0.001) placed.add(0, 0.0 to "Start")      // MP4 has no room before its first chapter
        return placed.mapIndexed { i, (at, title) -> OutputChapter(at, if (i + 1 < placed.size) placed[i + 1].first else total, title) }
    }
}
