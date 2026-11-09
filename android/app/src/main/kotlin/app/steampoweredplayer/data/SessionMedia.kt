// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min

/** Contiguous complete chunk numbers `first..last` (inclusive). A gap splits a session into several runs. */
data class SegmentRun(val first: Int, val last: Int) {
    val count: Int get() = last - first + 1
    operator fun contains(chunk: Int): Boolean = chunk in first..last
}

/**
 * The playable media of one recording folder. Chunk N starts at media time (N − 1) · [segmentSeconds] + [axisShiftSeconds] on the tfdt
 * axis: the shift is 0 for cache sessions and the real tfdt of chunk 1 for saved clips. Mirrors the desktop's `SessionMedia`.
 */
data class SessionMedia(
    val folder: String,
    val runs: List<SegmentRun>,
    val segmentSeconds: Double = DEFAULT_SEGMENT_SECONDS,
    val axisShiftSeconds: Double = 0.0,
    /** Saved clips only: where Steam meant the clip to end, from [ClipBounds]. Its last chunk runs 0 to 3 s past it. */
    val endLimitSeconds: Double? = null,
    /** Saved clips only: where Steam meant the clip to start. Its first chunk starts 0 to 3 s before it. */
    val startLimitSeconds: Double? = null,
) {
    val chunkCount: Int get() = runs.sumOf { it.count }
    val firstChunk: Int? get() = runs.firstOrNull()?.first
    val lastChunk: Int? get() = runs.lastOrNull()?.last

    /** The start of the playable media: the first chunk's start, or the clip's intended start when that is later. 0 without chunks. */
    val startSeconds: Double
        get() = firstChunk?.let { maxOf(chunkStartSeconds(it), startLimitSeconds ?: Double.NEGATIVE_INFINITY) } ?: 0.0

    /** The end of the playable media: the last chunk's end, or the clip's intended end when that is earlier. 0 without chunks. */
    val endSeconds: Double
        get() = lastChunk?.let { minOf(chunkEndSeconds(it), endLimitSeconds ?: Double.POSITIVE_INFINITY) } ?: 0.0

    /** Playable length: chunks × segment length, less what a saved clip's limits cut off its first and last chunks. Gaps aren't counted. */
    val durationSeconds: Double get() = chunkCount * segmentSeconds - trimmedHeadSeconds - trimmedTailSeconds

    val trimmedHeadSeconds: Double get() = firstChunk?.let { max(0.0, startSeconds - chunkStartSeconds(it)) } ?: 0.0
    val trimmedTailSeconds: Double get() = lastChunk?.let { max(0.0, chunkEndSeconds(it) - endSeconds) } ?: 0.0

    fun contains(chunk: Int): Boolean = runs.any { chunk in it }

    /** All complete chunk numbers in order. */
    fun chunks(): List<Int> = runs.flatMap { it.first..it.last }

    fun chunkStartSeconds(chunk: Int): Double = (chunk - 1) * segmentSeconds + axisShiftSeconds

    fun chunkEndSeconds(chunk: Int): Double = chunk * segmentSeconds + axisShiftSeconds

    /** The chunk whose interval holds [mediaSeconds] (it may not be on disk). NaN counts as the axis zero; clamped to ±1e9. */
    fun chunkAt(mediaSeconds: Double): Long {
        val t = if (mediaSeconds.isNaN()) axisShiftSeconds else mediaSeconds
        return (floor((t - axisShiftSeconds) / segmentSeconds) + 1).coerceIn(-1e9, 1e9).toLong()
    }

    companion object {
        const val DEFAULT_SEGMENT_SECONDS = 3.0

        /**
         * The axis shift for a recording whose chunk [chunk] starts at [tfdtSeconds]: the difference to (chunk − 1)·S, rounded to a whole number of
         * segments, as on the desktop. Real chunks start late by game-capture jitter, never by a fraction of a segment, so cache sessions give exactly 0
         * and clips give whole segments. Never below −(chunk − 1)·S. A tfdt that isn't finite or is negative gives 0.
         */
        fun axisShiftFor(tfdtSeconds: Double, chunk: Int, segmentSeconds: Double): Double {
            if (!tfdtSeconds.isFinite() || tfdtSeconds < 0 || segmentSeconds <= 0) return 0.0
            val raw = tfdtSeconds - (chunk - 1) * segmentSeconds
            val shift = max(Math.rint(raw / segmentSeconds) * segmentSeconds, -(chunk - 1) * segmentSeconds)
            return if (shift == 0.0) 0.0 else shift                                  // never −0
        }

        /** Group sorted, distinct numbers into runs: a gap starts a new run. */
        fun runsOf(sorted: List<Int>): List<SegmentRun> {
            val runs = ArrayList<SegmentRun>()
            if (sorted.isEmpty()) return runs
            var first = sorted[0]
            var previous = first
            for (i in 1 until sorted.size) {
                if (sorted[i] == previous + 1) {
                    previous = sorted[i]
                    continue
                }
                runs.add(SegmentRun(first, previous))
                first = sorted[i]
                previous = first
            }
            runs.add(SegmentRun(first, previous))
            return runs
        }

        /** The runs of [a] that also are in [b], splitting where needed. */
        fun intersect(a: List<SegmentRun>, b: List<SegmentRun>): List<SegmentRun> {
            val out = ArrayList<SegmentRun>()
            for (x in a) for (y in b) {
                val first = max(x.first, y.first)
                val last = min(x.last, y.last)
                if (first <= last) out.add(SegmentRun(first, last))
            }
            return out
        }
    }
}

/** Which chunks are complete, from a folder listing and head checks, as in the desktop's `ScanAnalysis`. Pure. */
object ChunkScan {
    /** Chunk files whose head must be read: numbers with both streams under final names. */
    fun headCandidates(fileNames: Iterable<String>): List<String> {
        val video = HashMap<Int, String>()
        val audio = HashMap<Int, String>()
        for (name in fileNames) {
            val chunk = FolderNames.parseChunk(name)?.takeUnless { it.temporary } ?: continue
            (if (chunk.stream == 0) video else audio)[chunk.number] = name
        }
        return video.keys.filter { it in audio }.sorted().flatMap { listOf(video.getValue(it), audio.getValue(it)) }
    }

    /** Complete = both streams without `.tmp` and both heads [HeadCheck.Valid]. [head] is only called for [headCandidates]. */
    fun complete(fileNames: Iterable<String>, head: (String) -> HeadCheck): List<Int> =
        headCandidates(fileNames).chunked(2).filter { (v, a) -> head(v) == HeadCheck.Valid && head(a) == HeadCheck.Valid }
            .map { (v, _) -> FolderNames.parseChunk(v)!!.number }
}
