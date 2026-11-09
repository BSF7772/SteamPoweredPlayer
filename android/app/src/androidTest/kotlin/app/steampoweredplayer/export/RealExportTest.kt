// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import android.os.SystemClock
import android.util.Log
import androidx.test.platform.app.InstrumentationRegistry
import app.steampoweredplayer.data.ChunkHeads
import app.steampoweredplayer.data.ChunkScan
import app.steampoweredplayer.data.ClipBounds
import app.steampoweredplayer.data.FirstFragment
import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.SessionMedia
import app.steampoweredplayer.data.readNBytesCompat
import app.steampoweredplayer.edit.AudioSync
import app.steampoweredplayer.edit.SyncAdvice
import app.steampoweredplayer.edit.SyncDetector
import java.io.File
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test

/**
 * Opt-in: real chunks through the real export code on this emulator or device, the files left in the test app's external files (`out/`) for the host to check by frame hash.
 * Push a media folder and pass `-e sppRealDir <name>`. Logs numbers only, under SppRealTest.
 */
class RealExportTest {
    private lateinit var media: SessionMedia
    private lateinit var out: File
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Before fun setUp() {
        val name = InstrumentationRegistry.getArguments().getString("sppRealDir")
        assumeTrue("no sppRealDir argument", !name.isNullOrEmpty())
        val root = context.getExternalFilesDir(null)!!
        val folder = File(root, name!!)
        assumeTrue("folder not there", folder.isDirectory)
        val names = folder.list().orEmpty().toList()
        val chunks = ChunkScan.complete(names) { n -> File(folder, n).inputStream().use { ChunkHeads.classify(it.readNBytesCompat(ChunkHeads.HEAD_LENGTH)) } }
        val init = File(folder, FolderNames.initFile(0)).readBytes()
        val head = File(folder, FolderNames.chunkFile(0, chunks.first())).inputStream().use { it.readNBytesCompat(FirstFragment.TFDT_HEAD_BYTES) }
        val shift = SessionMedia.axisShiftFor(FirstFragment.tfdtSeconds(init, head)!!, chunks.first(), 3.0)
        media = ClipBounds.apply(SessionMedia(folder.path, SessionMedia.runsOf(chunks), axisShiftSeconds = shift), File(folder, "session.mpd").takeIf { it.exists() }?.readText())
        out = File(root, "out").apply { mkdirs() }
    }

    private fun span(from: Double, to: Double) = ExportSpan(media.startSeconds + from, media.startSeconds + to)

    private fun lossless(name: String, spans: List<ExportSpan>, delay: Double = 0.0) {
        val t = SystemClock.elapsedRealtime()
        val file = File(out, "$name.mp4")
        LosslessExporter.export(media, spans, delay, emptyList(), file)
        Log.i(TAG, "$name: ${spans.joinToString { "%.6f..%.6f".format(it.start, it.end) }} delay $delay, ${file.length()} bytes, ${SystemClock.elapsedRealtime() - t} ms")
    }

    @Test fun save_clip_kinds() {
        lossless("whole", listOf(ExportSpan(media.startSeconds, media.endSeconds)))
        lossless("one", listOf(span(2.217, 8.854)))
        lossless("cuts", listOf(span(0.5, 1.7), span(2.3, 3.1), span(6.2, 8.9)))
        lossless("late", listOf(span(2.0, 7.0)), 0.5)
        lossless("early", listOf(span(2.0, 7.0)), -0.3)
    }

    @Test fun encoders_on_this_device() {
        DeviceEncoders.list().forEach { Log.i(TAG, "encoder ${it.name} ${it.mime} hw=${it.hardware} max ${it.maxWidth}x${it.maxHeight}") }
    }

    private fun encode(name: String, tierId: String, engine: LadderEngine, spans: List<ExportSpan>, edits: ReEncodeEdits = ReEncodeEdits(), targetMb: Double? = null) {
        val f = SourceFormat.read(media)
        val seconds = ExportSpans.outputSeconds(ExportSpans.plans(media, spans), edits.speed)
        val plan = ExportLadder.plan(ExportLadder.tier(tierId)!!, engine, DeviceEncoders.list(), f.width, f.height, f.fps, seconds, targetMb)
        if (plan == null) {
            Log.i(TAG, "$name: no encoder for $tierId on $engine at ${f.width}x${f.height}")
            return
        }
        val file = File(out, "$name.mp4")
        file.delete()
        val t = SystemClock.elapsedRealtime()
        runBlocking { ReEncoder(context, fastDecode = false).run(media, spans, plan, edits, File(context.cacheDir, "work-$name"), file) { } }
        val wall = SystemClock.elapsedRealtime() - t
        Log.i(TAG, "$name: ${plan.encoder} ${plan.mime} ${plan.width}x${plan.height} ${plan.videoBitsPerSecond} b/s, ${file.length()} bytes, $seconds s of output in $wall ms = ${"%.2f".format(seconds * 1000 / wall)}x, ${spans.joinToString { "%.6f..%.6f".format(it.start, it.end) }}")
        assertTrue(file.length() > 0)
    }

    @Test fun tiers() {
        val spans = listOf(span(2.217, 6.217))
        for (tier in ExportLadder.tiers + ExportLadder.specials) {
            encode("tier-${tier.id}", tier.id, LadderEngine.Auto, spans, targetMb = if (tier.id == ExportLadder.TARGET_ID) 2.0 else null)
        }
    }

    @Test fun re_encode_edits() {
        val best = ExportLadder.tiers.first { ExportLadder.plan(it, LadderEngine.Auto, DeviceEncoders.list(), 2560, 1440, 60.0, 4.0) != null }.id
        encode("enc-cuts", best, LadderEngine.Auto, listOf(span(0.5, 1.7), span(2.3, 3.1), span(6.2, 8.9)))
        encode("enc-speed2", best, LadderEngine.Auto, listOf(span(2.0, 6.0)), ReEncodeEdits(speed = 2.0))
        encode("enc-late", best, LadderEngine.Auto, listOf(span(2.0, 6.0)), ReEncodeEdits(audioDelay = 0.5))
        encode("enc-early", best, LadderEngine.Auto, listOf(span(2.0, 6.0)), ReEncodeEdits(audioDelay = -0.3))
        encode("enc-mute", best, LadderEngine.Auto, listOf(span(2.0, 4.0)), ReEncodeEdits(muted = true))
        encode("enc-quiet", best, LadderEngine.Auto, listOf(span(2.0, 4.0)), ReEncodeEdits(volumePercent = 40.0))
    }

    @Test fun detection_says_something_honest() {
        val t = SystemClock.elapsedRealtime()
        val r = SyncDetector.detect(media, ExportSpan(media.startSeconds, media.endSeconds))
        val advice = SyncAdvice.from(r)
        Log.i(TAG, "detect: late ${"%.1f".format(r.audioLate * 1000)} ms ${r.confidence} ${r.agreeing}/${r.consistent}, verdict ${advice.verdict}, ${SystemClock.elapsedRealtime() - t} ms")
        assertTrue(r.confidence == AudioSync.Confidence.High || advice.audioDelay == 0.0)
    }

    private companion object {
        const val TAG = "SppRealTest"
    }
}
