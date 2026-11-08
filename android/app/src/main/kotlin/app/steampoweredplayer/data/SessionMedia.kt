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
) {
    val chunkCount: Int get() = runs.sumOf { it.count }
    val firstChunk: Int? get() = runs.firstOrNull()?.first
    val lastChunk: Int? get() = runs.lastOrNull()?.last

    fun contains(chunk: Int): Boolean = runs.any { chunk in it }

    /** The chunk whose interval holds [mediaSeconds] (it may not be on disk). NaN counts as the axis zero; clamped to ±1e9. */
    fun chunkAt(mediaSeconds: Double): Long {
        val t = if (mediaSeconds.isNaN()) axisShiftSeconds else mediaSeconds
        return (floor((t - axisShiftSeconds) / segmentSeconds) + 1).coerceIn(-1e9, 1e9).toLong()
    }

    companion object {
        const val DEFAULT_SEGMENT_SECONDS = 3.0
    }
}
