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

    /** Each chunk's frames, as the moof tables give them: a chunk's frames start on its first one, a little past the grid. */
    private fun tables(all: DoubleArray, media: SessionMedia): (Int) -> DoubleArray? = { chunk ->
        all.filter { media.chunkAt(it - 0.040 - 0.002).toInt() == chunk || (chunk == 1 && it < 0.042) }.toDoubleArray()
    }

    private fun session(engine: FakeEngine, media: SessionMedia) = PlaybackSession(engine, scope, frameTimes = tables(engine.framesAt, media))

    private fun plain(engine: PlayerEngine) = PlaybackSession(engine, scope)

    private suspend fun kotlinx.coroutines.CompletableDeferred<Unit>.ok() = withTimeout(10_000) { await() }

    @Test fun frame_back_keeps_moving_across_chunk_boundaries() = runBlocking {
        val all = frames(6)
        val engine = FakeEngine(all)
        val s = session(engine, media(6))
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
        val s = session(engine, media(3))
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
        val s = session(engine, media(3))
        val start = all.indexOfFirst { it >= 4.0 }
        s.open(media(3), all[start], play = false).ok()
        val waits = List(5) { s.stepBack(frame) }
        waits.forEach { it.ok() }
        assertEquals(all[start - 5], engine.timePos, 1e-9)
    }

    @Test fun nothing_pauses_an_engine_that_is_already_paused() = runBlocking {
        val all = frames(3)
        val engine = FakeEngine(all)
        val s = session(engine, media(3))
        s.open(media(3), 3.5, play = false).ok()
        s.setPause(true)
        repeat(5) { s.stepBack(frame).ok() }
        s.frameStep()
        assertEquals(0, engine.pauseCalls)
    }

    @Test fun a_press_without_a_table_steps_nominally_and_the_next_is_exact_again() = runBlocking {
        val all = frames(3)
        val engine = FakeEngine(all)
        val m = media(3)
        var tableMissing = true
        val real = tables(all, m)
        val s = PlaybackSession(engine, scope, frameTimes = { c -> if (tableMissing) null else real(c) })
        val start = all.indexOfFirst { it >= 3.5 }
        s.open(m, all[start], play = false).ok()
        s.stepBack(frame).ok()
        assertTrue(engine.timePos < all[start])
        tableMissing = false
        val before = engine.timePos
        s.stepBack(frame).ok()
        assertEquals(all[all.indexOfFirst { abs(it - before) < 1e-9 } - 1], engine.timePos, 1e-9)
    }

    @Test fun a_back_step_loads_once_and_shows_nothing_on_the_way() = runBlocking {
        val all = frames(3)
        val engine = FakeEngine(all)
        val s = session(engine, media(3))
        val start = all.indexOfFirst { it >= 3.2 }
        s.open(media(3), all[start], play = false).ok()
        val loads = engine.loads
        var steps = 0
        engine.addTimeListener { steps++ }
        s.stepBack(frame).ok()
        assertEquals(loads + 1, engine.loads)
        assertEquals(0, steps)
    }

    @Test fun a_finishing_load_leaves_the_position_of_queued_presses_alone() = runBlocking {
        val all = frames(3)
        val engine = FakeEngine(all)
        val s = session(engine, media(3))
        val start = all.indexOfFirst { it >= 4.0 }
        s.open(media(3), all[start], play = false).ok()
        val waits = List(12) { s.stepBack(frame) }
        var last = s.currentPosition
        while (s.isReloading) {
            val now = s.currentPosition
            assertTrue("went forward: $last then $now", now <= last + 1e-9)
            last = now
            kotlinx.coroutines.delay(1)
        }
        waits.forEach { it.ok() }
        assertEquals(all[start - 12], engine.timePos, 1e-9)
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
        val s = plain(engine)
        s.open(media(1), 1.5, play = false).ok()
        s.stepBack(frame).ok()
        assertTrue("still at ${engine.timePos}", engine.timePos < 1.5 - 0.01 || s.currentPosition < 1.5 - 4 * frame)
    }

    @Test fun at_the_head_frame_back_stays_put() = runBlocking {
        val all = frames(1)
        val engine = FakeEngine(all)
        val s = session(engine, media(1))
        s.open(media(1), all[3], play = false).ok()
        repeat(6) { s.stepBack(frame).ok() }
        assertEquals(all[0], engine.timePos, 1e-9)
    }

    @Test fun seeks_during_a_reload_keep_only_the_latest_target() = runBlocking {
        val engine = FakeEngine(frames(10))
        val s = plain(engine)
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
        val s = plain(engine)
        val clip = SessionMedia("/m", listOf(SegmentRun(1, 4)), startLimitSeconds = 1.0, endLimitSeconds = 10.5)
        s.open(clip).ok()
        assertEquals(1.0, engine.loadStarts.last(), 0.0)
        assertEquals(10.5, engine.lastEnd!!, 0.0)
    }

    @Test fun a_loop_reloads_at_a_when_the_position_reaches_b() = runBlocking {
        val engine = FakeEngine(frames(4))
        val s = plain(engine)
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
        val s = plain(engine)
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

    @Test fun a_pause_during_a_load_outlives_the_load() = runBlocking {
        val all = frames(4)
        val real = FakeEngine(all)
        lateinit var s: PlaybackSession
        var pauseDuringLoad = false
        val engine = object : PlayerEngine by real {
            override suspend fun load(edl: String, startSeconds: Double, play: Boolean, endSeconds: Double?): Boolean {
                if (pauseDuringLoad) s.setPause(true)
                return real.load(edl, startSeconds, play, endSeconds)
            }
        }
        s = plain(engine)
        s.open(media(4), 1.0, play = true).ok()
        pauseDuringLoad = true
        val before = real.pauseCalls
        s.seek(5.0).ok()
        assertTrue(s.isPaused)
        assertTrue("told the engine {real.pauseCalls - before} times", real.pauseCalls - before >= 2)   // once when asked, once after the load that started playing
    }

    @Test fun each_load_says_whether_to_play() = runBlocking {
        val engine = FakeEngine(frames(4))
        val s = plain(engine)
        s.open(media(4), 1.0, play = false).ok()
        assertFalse(engine.lastPlay)
        s.setPause(false)
        s.seek(2.0).ok()
        assertTrue(engine.lastPlay)
    }

    @Test fun the_end_is_reported_once_and_a_reload_clears_it() = runBlocking {
        val engine = FakeEngine(frames(2))
        val s = plain(engine)
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
