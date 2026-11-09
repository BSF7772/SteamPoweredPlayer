// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/** What a press on the timeline takes hold of. */
enum class TimelineGrab { A, B, Seek }

/** Where the A-B handles start and how they move. Pure. */
object LoopHandles {
    const val MIN_SECONDS = 0.5
    private const val FIRST_RANGE_SECONDS = 40.0
    private const val FIRST_RANGE_SHARE = 0.25
    private const val BEFORE_PLAYHEAD_SHARE = 0.25

    /** The handles' first place: a range around the playhead, small enough on a short clip that both can be told apart and reached. */
    fun initial(position: Double, start: Double, end: Double): Pair<Double, Double> {
        val span = max(end - start, 0.0)
        val width = min(span, max(MIN_SECONDS, min(FIRST_RANGE_SECONDS, span * FIRST_RANGE_SHARE)))
        val a = (position - width * BEFORE_PLAYHEAD_SHARE).coerceIn(start, max(start, end - width))
        return a to a + width
    }

    fun moveA(seconds: Double, b: Double, start: Double): Double = seconds.coerceIn(start, max(start, b - MIN_SECONDS))

    fun moveB(seconds: Double, a: Double, end: Double): Double = seconds.coerceIn(min(end, a + MIN_SECONDS), end)

    /** The nearer handle within [reach] of the press, else a seek. Pixels, so a handle's touch area stays the same on any recording's length. */
    fun grab(x: Float, aX: Float?, bX: Float?, reach: Float): TimelineGrab {
        val toA = aX?.let { abs(x - it) } ?: Float.MAX_VALUE
        val toB = bX?.let { abs(x - it) } ?: Float.MAX_VALUE
        return when {
            toA > reach && toB > reach -> TimelineGrab.Seek
            toA <= toB -> TimelineGrab.A
            else -> TimelineGrab.B
        }
    }
}
