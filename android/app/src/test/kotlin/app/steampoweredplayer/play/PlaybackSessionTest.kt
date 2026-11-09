// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import app.steampoweredplayer.data.SegmentRun
import app.steampoweredplayer.data.SessionMedia
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.math.abs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** mpv's behaviour on a list of real-looking frame times: a load shows the first frame at or after the start, a step shows the next frame. */
private class FakeEngine(val framesAt: DoubleArray) : PlayerEngine {
    private val listeners = CopyOnWriteArrayList<(Double) -> Unit>()
    var loads = 0
    var pauseCalls = 0
    var loadStarts = ArrayList<Double>()
    var lastEdl = ""
    var lastPlay = false
    var lastEnd: Double? = null
    var dropNextSteps = 0
    var current = 0
    override var timePos = Double.NaN
    override var onEof: (() -> Unit)? = null

    override suspend fun load(edl: String, startSeconds: Double, play: Boolean, endSeconds: Double?): Boolean {
        loads++
        loadStarts.add(startSeconds)
        lastEdl = edl
        lastPlay = play
        lastEnd = endSeconds
        current = framesAt.indexOfFirst { it >= startSeconds }.let { if (it < 0) framesAt.lastIndex else it }
        timePos = framesAt[current]
        return true
    }

    override fun frameStep() {
        if (dropNextSteps > 0) {
            dropNextSteps--
            return
        }
        if (current < framesAt.lastIndex) current++
        timePos = framesAt[current]
        listeners.forEach { it(timePos) }
    }

    override fun setPause(paused: Boolean) {
        pauseCalls++
    }

    override fun addTimeListener(listener: (Double) -> Unit) {
        listeners.add(listener)
    }

    override fun removeTimeListener(listener: (Double) -> Unit) {
        listeners.remove(listener)
    }

    fun tick(t: Double) = listeners.forEach { it(t) }
}

class PlaybackSessionTest {
    private val scope = CoroutineScope(Dispatchers.Default + SupervisorJob())
    private val frame = 1.0 / 60

    /** 60 fps with Steam-like jitter; each chunk's first frame sits 40 ms off the 3 s grid, more than two frames. */
    private fun frames(chunks: Int): DoubleArray = buildList {
        for (c in 0 until chunks) {
            var t = c * 3.0 + 0.040
            while (t < (c + 1) * 3.0 + 0.04 - 1e-9) {
                add(t + if (size % 3 == 0) 0.002 else 0.0)
                t += frame
            }
        }
    }.toDoubleArray()

    private fun media(chunks: Int = 10) = SessionMedia("/m", listOf(SegmentRun(1, chunks)))

    private fun session(engine: PlayerEngine) = PlaybackSession(engine, scope, frameProbeTimeoutMillis = 100)

    private suspend fun kotlinx.coroutines.CompletableDeferred<Unit>.ok() = withTimeout(10_000) { await() }

    @Test fun frame_back_keeps_moving_across_chunk_boundaries() = runBlocking {
        val all = frames(6)
        val engine = FakeEngine(all)
        val s = session(engine)
        val start = all.indexOfFirst { it >= 9.1 }
        s.open(media(6), all[start], play = false).ok()
        repeat(450) { s.stepBack(frame).ok() }                                        // past three chunk starts
        val moved = start - all.indexOfFirst { it >= s.currentPosition - 1e-9 }
        assertTrue("moved $moved", moved >= 445)
        assertEquals(all[start - moved], engine.timePos, 1e-9)
    }

    @Test fun each_frame_back_lands_on_the_real_previous_frame() = runBlocking {
        val all = frames(3)
        val engine = FakeEngine(all)
        val s = session(engine)
        val start = all.indexOfFirst { it >= 3.2 }
        s.open(media(3), all[start], play = false).ok()
        for (k in 1..30) {
            s.stepBack(frame).ok()
            assertEquals("press $k", all[start - k], engine.timePos, 1e-9)
        }
    }

    @Test fun presses_during_a_reload_add_up() = runBlocking {
        val all = frames(3)
        val engine = FakeEngine(all)
        val s = session(engine)
        val start = all.indexOfFirst { it >= 4.0 }
        s.open(media(3), all[start], play = false).ok()
        val waits = List(5) { s.stepBack(frame) }
        waits.forEach { it.ok() }
        assertEquals(all[start - 5], engine.timePos, 1e-9)
    }

    @Test fun nothing_pauses_an_engine_that_is_already_paused() = runBlocking {
        val all = frames(3)
        val engine = FakeEngine(all)
        val s = session(engine)
        s.open(media(3), 3.5, play = false).ok()
        s.setPause(true)
        repeat(5) { s.stepBack(frame).ok() }
        s.frameStep()
        assertEquals(0, engine.pauseCalls)
    }

    @Test fun a_probe_timeout_costs_one_press_and_exact_steps_come_back() = runBlocking {
        val all = frames(3)
        val engine = FakeEngine(all)
        val s = session(engine)
        val start = all.indexOfFirst { it >= 3.5 }
        s.open(media(3), all[start], play = false).ok()
        repeat(6) {
            engine.dropNextSteps = 1                                                  // the step's report never comes, repeatedly
            s.stepBack(frame).ok()
        }
        assertTrue(s.currentPosition < all[start])
        engine.dropNextSteps = 0
        val before = engine.timePos
        s.stepBack(frame).ok()
        val i = all.indexOfFirst { abs(it - before) < 1e-9 }
        assertEquals(all[i - 1], engine.timePos, 1e-9)
    }

    @Test fun a_load_that_lands_on_the_same_frame_steps_further() = runBlocking {
        // A sparse stretch: nominal steps can't reach the previous frame and no probe works, because steps never report.
        val all = doubleArrayOf(0.0, 0.5, 1.0, 1.5, 2.0)
        val engine = object : PlayerEngine by FakeEngine(all) {
            override var timePos = Double.NaN
            override suspend fun load(edl: String, startSeconds: Double, play: Boolean, endSeconds: Double?): Boolean {
                timePos = all.first { it >= startSeconds - 1e-9 }
                return true
            }
            override fun frameStep() = Unit
        }
        val s = session(engine)
        s.open(media(1), 1.5, play = false).ok()
        s.stepBack(frame).ok()
        assertTrue("still at ${engine.timePos}", engine.timePos < 1.5 - 0.01 || s.currentPosition < 1.5 - 4 * frame)
    }

    @Test fun at_the_head_frame_back_stays_put() = runBlocking {
        val all = frames(1)
        val engine = FakeEngine(all)
        val s = session(engine)
        s.open(media(1), all[3], play = false).ok()
        repeat(6) { s.stepBack(frame).ok() }
        assertEquals(all[0], engine.timePos, 1e-9)
    }

    @Test fun seeks_during_a_reload_keep_only_the_latest_target() = runBlocking {
        val engine = FakeEngine(frames(10))
        val s = session(engine)
        s.open(media(), 1.0).ok()
        val before = engine.loads
        val a = s.seek(5.0)
        val b = s.seek(9.0)
        val c = s.seek(20.0)
        listOf(a, b, c).forEach { it.ok() }
        assertTrue(engine.loads - before <= 2)
        assertEquals(20.0, engine.loadStarts.last(), 1e-9)
    }

    @Test fun targets_snap_to_what_exists() {
        val m = SessionMedia("/m", listOf(SegmentRun(1, 4), SegmentRun(8, 9)))
        assertEquals(0.0, PlaybackSession.snap(m, -5.0), 0.0)
        assertEquals(0.0, PlaybackSession.snap(m, Double.NaN), 0.0)
        assertEquals(21.0, PlaybackSession.snap(m, 15.0), 0.0)                        // in the gap: the next run
        assertEquals(11.7, PlaybackSession.snap(m, 11.9), 1e-9)                       // the last frames before a gap
        assertEquals(26.95, PlaybackSession.snap(m, 90.0), 1e-9)
    }

    @Test fun a_saved_clips_end_limit_goes_to_the_engine() = runBlocking {
        val engine = FakeEngine(frames(4))
        val s = session(engine)
        val clip = SessionMedia("/m", listOf(SegmentRun(1, 4)), startLimitSeconds = 1.0, endLimitSeconds = 10.5)
        s.open(clip).ok()
        assertEquals(1.0, engine.loadStarts.last(), 0.0)
        assertEquals(10.5, engine.lastEnd!!, 0.0)
    }

    @Test fun a_loop_reloads_at_a_when_the_position_reaches_b() = runBlocking {
        val engine = FakeEngine(frames(4))
        val s = session(engine)
        s.open(media(4), 1.0).ok()
        s.setLoop(2.0, 4.0)
        engine.tick(3.0)
        val loads = engine.loads
        engine.tick(4.0)
        withTimeout(5_000) { while (engine.loads == loads || s.isReloading) kotlinx.coroutines.delay(5) }
        assertEquals(2.0, engine.loadStarts.last(), 1e-9)
    }

    @Test fun a_seek_outside_the_loop_plays_on() = runBlocking {
        val engine = FakeEngine(frames(4))
        val s = session(engine)
        s.open(media(4), 1.0).ok()
        s.setLoop(2.0, 4.0)
        s.seek(6.0).ok()
        assertFalse(s.isLoopEngaged)
        val loads = engine.loads
        engine.tick(6.5)
        assertEquals(loads, engine.loads)
        s.seek(3.0).ok()
        assertTrue(s.isLoopEngaged)
    }

    @Test fun each_load_says_whether_to_play() = runBlocking {
        val engine = FakeEngine(frames(4))
        val s = session(engine)
        s.open(media(4), 1.0, play = false).ok()
        assertFalse(engine.lastPlay)
        s.setPause(false)
        s.seek(2.0).ok()
        assertTrue(engine.lastPlay)
    }

    @Test fun the_end_is_reported_once_and_a_reload_clears_it() = runBlocking {
        val engine = FakeEngine(frames(2))
        val s = session(engine)
        var ended = 0
        s.onReachedEnd = { ended++ }
        s.open(media(2), 1.0).ok()
        engine.onEof?.invoke()
        assertEquals(1, ended)
        assertTrue(s.reachedEnd)
        s.seek(1.0).ok()
        assertFalse(s.reachedEnd)
    }
}
