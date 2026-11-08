// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import kotlin.math.floor

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

    fun contains(chunk: Int): Boolean = runs.any { chunk in it }

    fun chunkStartSeconds(chunk: Int): Double = (chunk - 1) * segmentSeconds + axisShiftSeconds

    fun chunkEndSeconds(chunk: Int): Double = chunk * segmentSeconds + axisShiftSeconds

    /** The chunk whose interval holds [mediaSeconds] (it may not be on disk). NaN counts as the axis zero; clamped to ±1e9. */
    fun chunkAt(mediaSeconds: Double): Long {
        val t = if (mediaSeconds.isNaN()) axisShiftSeconds else mediaSeconds
        return (floor((t - axisShiftSeconds) / segmentSeconds) + 1).coerceIn(-1e9, 1e9).toLong()
    }

    companion object {
        const val DEFAULT_SEGMENT_SECONDS = 3.0
    }
}
