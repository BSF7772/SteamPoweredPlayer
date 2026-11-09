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

    /** One press of a move button: about 15 frames, so a handle is placed quickly but still finely on a small screen. */
    const val NUDGE_FRAMES = 15

    /** The outer buttons and a double tap jump a selected handle by one of Steam's 3 s chunks, which is how far apart events tend to be told. */
    const val JUMP_SECONDS = 3.0

    /** How long a press is held before it starts repeating, and how often it repeats then. */
    const val REPEAT_AFTER_MILLIS = 400L
    const val REPEAT_EVERY_MILLIS = 100L

    /**
     * Frames per repeat while a move button is held: 15 at first, then faster the longer it's held, so an hour-long cache can be crossed without lifting the finger. At 60 fps
     * that's 2.5 s, 10 s, 50 s, then 200 s of recording per second of holding.
     */
    fun heldFrames(heldMillis: Long): Int = when {
        heldMillis < 1_000 -> NUDGE_FRAMES
        heldMillis < 2_500 -> 60
        heldMillis < 5_000 -> 300
        else -> 1_200
    }

    /** Seconds per frame from the chunks' frame rate; a missing or silly rate counts as 60 fps. */
    fun frameSeconds(fps: Double): Double = if (fps.isFinite() && fps in 1.0..1000.0) 1.0 / fps else 1.0 / 60.0

    /** The selected handle moved by [deltaSeconds], held inside the recording and at least [MIN_SECONDS] from the other handle. Returns the new (A, B). */
    fun nudge(handle: TimelineGrab, deltaSeconds: Double, a: Double, b: Double, start: Double, end: Double): Pair<Double, Double> = when (handle) {
        TimelineGrab.A -> moveA(a + deltaSeconds, b, start) to b
        TimelineGrab.B -> a to moveB(b + deltaSeconds, a, end)
        TimelineGrab.Seek -> a to b
    }

    /** Where the picture goes to show a handle: A's own frame, and for B the last frame the range keeps, since B itself is left out ([A,B)). */
    fun shownAt(handle: TimelineGrab, a: Double, b: Double, frameSeconds: Double): Double = when (handle) {
        TimelineGrab.B -> max(a, b - frameSeconds)
        else -> a
    }

    /** What a press that lifted without moving does to the selection: a tap on the selected handle lets go of it, a tap on the other selects that one, a tap elsewhere lets go. */
    fun tapped(current: TimelineGrab?, grab: TimelineGrab): TimelineGrab? = when {
        grab == TimelineGrab.Seek -> null
        current == grab -> null
        else -> grab
    }
}
