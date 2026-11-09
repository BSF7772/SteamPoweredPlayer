// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import app.steampoweredplayer.data.SegmentRun
import app.steampoweredplayer.data.SessionMedia
import kotlin.math.floor

/** What the player can play from the chunks on the phone, and what to fetch next. Pure. */
object PlayableRuns {
    /**
     * [full] narrowed to the contiguous stretch of [available] chunks that holds [mediaSeconds]: the player only reads local files, and stops at the download edge instead
     * of running into a chunk that's still coming. Null when the chunk at that time isn't here.
     */
    fun around(full: SessionMedia, available: Set<Int>, mediaSeconds: Double): SessionMedia? {
        val target = full.chunkAt(mediaSeconds).toInt()
        val run = full.runs.firstOrNull { target in it } ?: return null
        if (target !in available) return null
        var first = target
        var last = target
        while (first - 1 >= run.first && first - 1 in available) first--
        while (last + 1 <= run.last && last + 1 in available) last++
        return full.copy(runs = listOf(SegmentRun(first, last)))
    }

    /** The chunk at [mediaSeconds], or the first after it when that time lies in a gap of the recording. Null past the end. */
    fun nextChunkAt(full: SessionMedia, mediaSeconds: Double): Int? {
        val target = full.chunkAt(mediaSeconds)
        for (run in full.runs) {
            if (target <= run.first) return run.first
            if (target <= run.last) return target.toInt()
        }
        return null
    }

    /** The chunk after [chunk] in the recording, across a gap; null at the end. */
    fun chunkAfter(full: SessionMedia, chunk: Int): Int? {
        for (run in full.runs) {
            if (chunk < run.first) return run.first
            if (chunk < run.last) return chunk + 1
        }
        return null
    }
}

/** Which chunks the player asks for. A clip or a short session comes whole; a long one follows the playhead, so opening it doesn't spend gigabytes. Pure. */
object FetchPolicy {
    const val WHOLE_BELOW_BYTES = 1024L * 1024 * 1024
    const val AHEAD_CHUNKS = 40
    const val TOP_UP_BELOW_CHUNKS = 20

    fun selection(full: SessionMedia, sizeBytes: Long, playheadChunk: Int): List<Int> {
        val all = full.chunks()
        if (sizeBytes in 1 until WHOLE_BELOW_BYTES || all.size <= AHEAD_CHUNKS) return all
        val from = all.indexOfFirst { it >= playheadChunk }.let { if (it < 0) all.lastIndex else it }
        return all.subList(from, minOf(all.size, from + AHEAD_CHUNKS))
    }

    /** Time to ask for more: fewer than [TOP_UP_BELOW_CHUNKS] chunks are here ahead of the playhead, and more exist. */
    fun needsTopUp(full: SessionMedia, available: Set<Int>, playheadChunk: Int): Boolean {
        val ahead = full.chunks().filter { it >= playheadChunk }
        return ahead.size > ahead.count { it in available } && ahead.take(TOP_UP_BELOW_CHUNKS).any { it !in available }
    }
}

/** Times for the screen. */
object TimeText {
    fun of(seconds: Double): String {
        val total = floor(seconds.coerceAtLeast(0.0)).toLong()
        val h = total / 3600
        val m = total % 3600 / 60
        val s = total % 60
        return if (h > 0) "%d:%02d:%02d".format(h, m, s) else "%d:%02d".format(m, s)
    }
}
