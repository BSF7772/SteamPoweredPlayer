// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import android.graphics.ImageFormat
import android.graphics.PixelFormat
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import androidx.test.platform.app.InstrumentationRegistry
import app.steampoweredplayer.data.ChunkScan
import app.steampoweredplayer.data.ChunkHeads
import app.steampoweredplayer.data.ClipBounds
import app.steampoweredplayer.data.FirstFragment
import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.SessionMedia
import app.steampoweredplayer.data.readNBytesCompat
import java.io.File
import kotlin.coroutines.resume
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test

/**
 * Opt-in: real chunks through real libmpv on this emulator or device. Push a media folder to the test app's external files and pass `-e sppRealDir <name>`.
 * The picture goes to an off-screen surface. Logs numbers only, under SppRealTest.
 */
class RealPlaybackTest {
    private val scope = CoroutineScope(Dispatchers.Default + SupervisorJob())
    private lateinit var engine: MpvEngine
    private lateinit var thread: HandlerThread
    private lateinit var reader: ImageReader
    private lateinit var media: SessionMedia

    @Before fun setUp() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val name = InstrumentationRegistry.getArguments().getString("sppRealDir")
        assumeTrue("no sppRealDir argument", !name.isNullOrEmpty())
        val context = instrumentation.targetContext
        val folder = File(context.getExternalFilesDir(null), name!!)
        assumeTrue("folder not there", folder.isDirectory)
        media = loadMedia(folder)
        thread = HandlerThread("frames").apply { start() }
        reader = ImageReader.newInstance(1280, 720, PixelFormat.RGBX_8888, 4)
        reader.setOnImageAvailableListener({ it.acquireLatestImage()?.close() }, Handler(thread.looper))
        val decode = if (InstrumentationRegistry.getArguments().getString("sppDecode") == "cpu") DecodeMode.Cpu else DecodeMode.Gpu
        engine = MpvEngine(context, decode)
        engine.attachSurface(reader.surface)
        engine.surfaceSize(1280, 720)
    }

    @After fun tearDown() {
        if (::engine.isInitialized) {
            engine.detachSurface()
            engine.destroy()
            reader.close()
            thread.quitSafely()
        }
    }

    private fun loadMedia(folder: File): SessionMedia {
        val names = folder.list().orEmpty().toList()
        val chunks = ChunkScan.complete(names) { n -> File(folder, n).inputStream().use { ChunkHeads.classify(it.readNBytesCompat(ChunkHeads.HEAD_LENGTH)) } }
        val first = chunks.first()
        val init = File(folder, FolderNames.initFile(0)).readBytes()
        val head = File(folder, FolderNames.chunkFile(0, first)).inputStream().use { it.readNBytesCompat(FirstFragment.TFDT_HEAD_BYTES) }
        val tfdt = FirstFragment.tfdtSeconds(init, head)!!
        val base = SessionMedia(folder.path, SessionMedia.runsOf(chunks), axisShiftSeconds = SessionMedia.axisShiftFor(tfdt, first, 3.0))
        return ClipBounds.apply(base, File(folder, "session.mpd").takeIf { it.exists() }?.readText())
    }

    private suspend fun kotlinx.coroutines.CompletableDeferred<Unit>.ok() = withTimeout(30_000) { await() }

    @Test fun opens_seeks_and_plays_real_chunks() = runBlocking {
        val s = PlaybackSession(engine, scope)
        val t0 = SystemClock.elapsedRealtime()
        s.open(media, play = false).ok()
        val open = SystemClock.elapsedRealtime() - t0
        Log.i(TAG, "open: $open ms, decoder=${engine.hwdecCurrent()}, media ${media.startSeconds}..${media.endSeconds}, shift ${media.axisShiftSeconds}")
        assertEquals(media.startSeconds, engine.timePos, 0.1)

        val seeks = ArrayList<Long>()
        repeat(12) { i ->
            val target = media.startSeconds + (media.endSeconds - media.startSeconds) * (((i * 7) % 12) + 0.5) / 12
            val t = SystemClock.elapsedRealtime()
            s.seek(target).ok()
            seeks.add(SystemClock.elapsedRealtime() - t)
            assertEquals("seek $i", target, engine.timePos, 0.05)
        }
        Log.i(TAG, "seek: median ${seeks.sorted()[seeks.size / 2]} ms, max ${seeks.max()} ms")

        s.seek(media.startSeconds).ok()
        val before = engine.stat("frame-drop-count") ?: 0.0
        val decoderDrops = engine.stat("decoder-frame-drop-count") ?: 0.0
        val wall = SystemClock.elapsedRealtime()
        s.setPause(false)
        delay(8_000)
        val advanced = engine.timePos - media.startSeconds
        val seconds = (SystemClock.elapsedRealtime() - wall) / 1000.0
        Log.i(
            TAG,
            "play: ${"%.2f".format(advanced)} s of media in ${"%.2f".format(seconds)} s, vo drops ${(engine.stat("frame-drop-count") ?: 0.0) - before}, " +
                "decoder drops ${(engine.stat("decoder-frame-drop-count") ?: 0.0) - decoderDrops}, fps ${engine.stat("estimated-display-fps")}, decoder=${engine.hwdecCurrent()}",
        )
        assertTrue("advanced $advanced in $seconds", advanced > seconds * 0.8 || advanced > media.durationSeconds - 1)
        s.close()
    }

    @Test fun what_a_reload_costs() = runBlocking {
        val s = PlaybackSession(engine, scope)
        val chunk = media.chunks()[1]
        s.open(media, media.chunkStartSeconds(chunk), play = false).ok()
        for (into in listOf(0.0, 0.0, 0.5, 1.0, 2.0, 2.9, 0.0, 2.9)) {
            val t = SystemClock.elapsedRealtime()
            s.seek(media.chunkStartSeconds(chunk) + into + 1e-3).ok()
            Log.i(TAG, "reload ${into} s into a chunk: ${SystemClock.elapsedRealtime() - t} ms")
        }
        s.close()
    }

    @Test fun frame_back_crosses_chunk_boundaries() = runBlocking {
        val s = PlaybackSession(engine, scope)
        // 0.2 s past a chunk's start, so that 200 frames cross two boundaries.
        val chunk = media.chunks().last()
        val start = media.chunkStartSeconds(chunk) + 0.2
        assumeTrue("needs three chunks", media.chunkCount >= 3)
        s.open(media, start, play = false).ok()
        val from = engine.timePos
        val times = ArrayList<Long>()
        val seen = linkedSetOf(from)
        repeat(PRESSES) {
            val t = SystemClock.elapsedRealtime()
            s.stepBack().ok()
            times.add(SystemClock.elapsedRealtime() - t)
            seen.add(engine.timePos)
        }
        val moved = seen.size - 1
        val chunksCrossed = media.chunkAt(from) - media.chunkAt(engine.timePos)
        Log.i(TAG, "frame back: $moved distinct frames in $PRESSES presses, ${"%.3f".format(from - engine.timePos)} s back, $chunksCrossed boundaries, median ${times.sorted()[times.size / 2]} ms, max ${times.max()} ms")
        assertTrue("moved $moved", moved >= PRESSES - 5)
        assertTrue("crossed $chunksCrossed", chunksCrossed >= 2)
        s.close()
    }

    @Test fun frame_back_presses_at_a_fixed_rate_do_not_get_stuck() = runBlocking {
        val s = PlaybackSession(engine, scope)
        val chunk = media.chunks().last()
        val start = media.chunkStartSeconds(chunk) + 0.2
        s.open(media, start, play = false).ok()
        val from = engine.timePos
        repeat(60) {
            s.stepBack()
            delay(180)
            if (it % 10 == 0) Log.i(TAG, "press $it: position ${"%.3f".format(from - s.currentPosition)} back, on screen ${"%.3f".format(from - engine.timePos)} back")
        }
        withTimeout(30_000) { while (s.isReloading) delay(20) }
        val back = from - engine.timePos
        Log.i(TAG, "frame back at 180 ms: ${"%.3f".format(back)} s back after 60 presses (1 s expected)")
        assertTrue("back $back", back > 0.9)
        s.close()
    }

    @Test fun forward_step_then_back_returns_to_the_same_frame() = runBlocking {
        val s = PlaybackSession(engine, scope)
        s.open(media, media.chunkStartSeconds(media.chunks()[1]) + 1.0, play = false).ok()
        val start = engine.timePos
        val listener = CompletableList()
        engine.addTimeListener(listener)
        s.frameStep()
        withTimeout(5_000) { while (engine.timePos <= start) delay(5) }
        val next = engine.timePos
        s.stepBack().ok()
        engine.removeTimeListener(listener)
        assertEquals(start, engine.timePos, 1e-4)
        Log.i(TAG, "step: ${"%.6f".format(next - start)} s forward, back to the same frame")
        s.close()
    }

    private class CompletableList : (Double) -> Unit {
        override fun invoke(p1: Double) = Unit
    }

    private companion object {
        const val TAG = "SppRealTest"
        const val PRESSES = 200
    }
}
