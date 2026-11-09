// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import app.steampoweredplayer.data.SessionMedia
import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min

/** One MP4 edit: [duration] on the movie's timescale; [mediaTime] on the track's, −1 for a stretch with nothing. */
data class EditEntry(val duration: Long, val mediaTime: Long)

data class Mp4Timescales(val movie: Long, val video: Long, val audio: Long)

/** A kept range on the source axis. An open end also keeps the frames a few ms past it, at the recording's own first or last chunk. */
data class LosslessRange(val start: Double, val end: Double, val openHead: Boolean = false, val openTail: Boolean = false)

/** A stored track as two parallel lists in file order: each packet's pts in seconds on the source axis, and in ticks on the file's media axis. */
class StoredPackets(val source: DoubleArray, val ticks: LongArray)

/**
 * The edit lists that make a file show exactly the source frames in each kept range, in order, with the sound that belongs to them, as in the desktop's `LosslessEdits`.
 * The file holds whole chunks because a chunk's first frame is the only keyframe, and with B-frames a frame shown before B can need one stored after it. Pure.
 */
object LosslessEdits {
    private const val START_TOLERANCE = 0.001                                         // a frame stamped exactly on A is kept
    private const val FRAME_LENGTH_FALLBACK = 1.0 / 60
    private const val GAP_SECONDS = 0.25                                              // a next frame this far off belongs to another stored chunk

    fun rangeOf(media: SessionMedia, plan: ExportPlan) = LosslessRange(
        plan.start,
        plan.end,
        plan.firstChunk == media.firstChunk && plan.start <= plan.snappedStart + 0.001,
        plan.lastChunk == media.lastChunk && plan.end >= plan.snappedEnd - 0.001,
    )

    /** [audioDelay]: seconds to delay the sound against the picture, negative = earlier. Empty lists when no frame falls in any range. */
    fun compute(ranges: List<LosslessRange>, video: StoredPackets, audio: StoredPackets, scales: Mp4Timescales, audioDelay: Double): Pair<List<EditEntry>, List<EditEntry>> {
        check(video.source.size == video.ticks.size && audio.source.size == audio.ticks.size) { "The file holds a different number of packets than the source." }
        val shown = video.source.indices.sortedBy { video.source[it] }                // presentation order
        val times = DoubleArray(shown.size) { video.source[shown[it]] }
        val picture = ArrayList<EditEntry>(ranges.size)
        val sound = ArrayList<EditEntry>(ranges.size)
        ranges.forEachIndexed { r, range ->
            val first = if (range.openHead) 0 else lowerBound(times, range.start - START_TOLERANCE)
            val next = if (range.openTail) times.size else lowerBound(times, range.end)
            if (next <= first) return@forEachIndexed
            val startTick = video.ticks[shown[first]]
            val lastTick = video.ticks[shown[next - 1]]
            val endTick = if (next < times.size && times[next] - times[next - 1] < GAP_SECONDS) {
                video.ticks[shown[next]]
            } else {
                lastTick + Math.round(frameLength(times, next - 1) * scales.video)
            }
            // One movie tick short, so rounding can't let the next frame in.
            val duration = max(1, (endTick - startTick) * scales.movie / scales.video - 1)
            picture.add(EditEntry(duration, startTick))
            // The sound of the last range runs to B even if the picture stops short of it.
            val last = r == ranges.lastIndex
            val soundLength = if (last && !range.openTail) max(duration, ((range.end - times[first]) * scales.movie).toLong()) else duration
            addSound(sound, audio.source, audio.ticks, times[first] - audioDelay, soundLength, scales, last)
        }
        return picture to sound
    }

    private fun addSound(sound: MutableList<EditEntry>, times: DoubleArray, ticks: LongArray, fromSource: Double, duration: Long, scales: Mp4Timescales, last: Boolean) {
        var remaining = duration
        var from = fromSource
        if (times.isNotEmpty()) {
            if (from < times[0]) {                                                    // the recording's sound starts later: silence until then
                val lead = min(remaining, Math.round((times[0] - from) * scales.movie))
                if (lead > 0) {
                    sound.add(EditEntry(lead, -1))
                    remaining -= lead
                }
                from = times[0]
            }
            val length = if (times.size > 1) times[times.size - 1] - times[times.size - 2] else 1024.0 / max(1, scales.audio)
            val end = times.last() + length
            if (remaining > 0 && from < end) {
                val j = max(0, upperBound(times, from) - 1)
                val play = min(remaining, floor((end - from) * scales.movie).toLong())
                if (play > 0) {
                    sound.add(EditEntry(play, ticks[j] + Math.round((from - times[j]) * scales.audio)))
                    remaining -= play
                }
            }
        }
        if (remaining > 0 && !last) sound.add(EditEntry(remaining, -1))               // keeps the next range's sound on its picture
    }

    private fun frameLength(times: DoubleArray, i: Int): Double {
        val d = if (i > 0) times[i] - times[i - 1] else FRAME_LENGTH_FALLBACK
        return if (d > 0 && d < GAP_SECONDS) d else FRAME_LENGTH_FALLBACK
    }

    private fun lowerBound(a: DoubleArray, v: Double): Int {
        var lo = 0
        var hi = a.size
        while (lo < hi) {
            val m = (lo + hi) ushr 1
            if (a[m] < v) lo = m + 1 else hi = m
        }
        return lo
    }

    private fun upperBound(a: DoubleArray, v: Double): Int {
        var lo = 0
        var hi = a.size
        while (lo < hi) {
            val m = (lo + hi) ushr 1
            if (a[m] <= v) lo = m + 1 else hi = m
        }
        return lo
    }
}
