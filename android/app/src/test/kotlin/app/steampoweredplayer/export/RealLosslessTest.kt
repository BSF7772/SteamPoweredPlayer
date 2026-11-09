// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import app.steampoweredplayer.data.ClipBounds
import app.steampoweredplayer.data.FirstFragment
import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.SessionMedia
import java.io.File
import java.nio.file.Files
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Opt-in and read-only: `SPP_REAL_LIBRARY` is a BackgroundRecordPath, `SPP_REAL_CLIP` a clip's folder name, ffmpeg on PATH. Compares the frames an export shows with the source
 * frames in [A,B) by MD5. Prints numbers only; the files go to the temp folder and are removed.
 */
class RealLosslessTest {
    private val work: File = Files.createTempDirectory("spp-lossless").toFile()

    private val folder: File by lazy {
        val root = System.getenv("SPP_REAL_LIBRARY")
        val clip = System.getenv("SPP_REAL_CLIP")
        assumeTrue("SPP_REAL_LIBRARY and SPP_REAL_CLIP are not set", !root.isNullOrEmpty() && !clip.isNullOrEmpty())
        File(root, "clips/$clip/video").listFiles()!!.first { it.isDirectory }
    }

    private val media: SessionMedia by lazy {
        val chunks = folder.list()!!.mapNotNull { FolderNames.parseChunk(it) }.filter { it.stream == 0 && !it.temporary }.map { it.number }.sorted()
        val init = File(folder, FolderNames.initFile(0)).readBytes()
        val head = File(folder, FolderNames.chunkFile(0, chunks.first())).inputStream().use { it.readNBytes(FirstFragment.TFDT_HEAD_BYTES) }
        val shift = SessionMedia.axisShiftFor(FirstFragment.tfdtSeconds(init, head)!!, chunks.first(), 3.0)
        ClipBounds.apply(SessionMedia(folder.path, SessionMedia.runsOf(chunks), axisShiftSeconds = shift), File(folder, "session.mpd").takeIf { it.exists() }?.readText())
    }

    private fun run(vararg args: String): List<String> {
        val p = ProcessBuilder(*args).redirectErrorStream(false).start()
        val out = p.inputStream.bufferedReader().readLines()
        p.errorStream.bufferedReader().readText()
        assertEquals(args.joinToString(" "), 0, p.waitFor())
        return out
    }

    /** (pts, md5) of every frame `file` shows. The source is read with its absolute timestamps. */
    private fun frames(file: File): List<Pair<Double, String>> {
        val lines = run("ffmpeg", "-v", "error", "-copyts", "-i", file.path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-")
        val hashes = lines.filterNot { it.startsWith("#") }.map { it.split(",").last().trim() }
        // framemd5's own time base rounds to about a millisecond: the times come from ffprobe, frame for frame in the same order.
        val times = run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-of", "csv=p=0", file.path)
            .filter { it.isNotBlank() }.map { it.trim(',', ' ').toDouble() }
        assertEquals("ffmpeg and ffprobe disagree on the frames", hashes.size, times.size)
        return times.zip(hashes)
    }

    private fun source(): File {
        val out = File(work, "source.mp4")
        out.outputStream().use { o ->
            File(folder, FolderNames.initFile(0)).inputStream().use { it.copyTo(o) }
            for (n in media.chunks()) File(folder, FolderNames.chunkFile(0, n)).inputStream().use { it.copyTo(o) }
        }
        return out
    }

    private fun check(name: String, spans: List<ExportSpan>, delay: Double = 0.0) {
        val src = frames(source()).sortedBy { it.first }
        val out = File(work, "$name.mp4")
        val result = LosslessExporter.export(media, spans, delay, emptyList(), out)
        val plans = ExportSpans.plans(media, spans)
        val expected = plans.flatMap { p ->
            val range = LosslessEdits.rangeOf(media, p)
            src.filter { (t, _) -> (range.openHead || t >= p.start - 0.001) && (range.openTail || t < p.end) }.map { it.second }
        }
        val shown = frames(out).map { it.second }
        println("$name: expected ${expected.size} frames, shown ${shown.size}, size ${out.length()}, edits ${result.videoEdits.size}/${result.audioEdits.size}")
        val missing = expected.filterNot { it in shown.toSet() }
        src.filter { it.second in missing }.forEach { (t, _) ->
            val p = plans.firstOrNull { t >= it.start - 0.1 && t < it.end + 0.1 }
            println("$name: missing frame at +${"%.4f".format(t - (p?.start ?: 0.0))} of ${"%.4f".format((p?.end ?: 0.0) - (p?.start ?: 0.0))}")
        }
        assertEquals("$name frame count", expected.size, shown.size)
        assertEquals("$name frames", expected, shown)

        val audioEnd = run("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", out.path)
            .filter { it.isNotBlank() }.map { l -> l.split(",").let { it[0].toDouble() + it[1].toDouble() } }.max()
        val want = plans.sumOf { it.end - it.start }
        println("$name: sound's last packet ends at ${"%.3f".format(audioEnd)}, picture span ${"%.3f".format(want)}")
    }

    @Test fun one_span_inside_a_clip() {
        val s = media.startSeconds
        check("one", listOf(ExportSpan(s + 2.217, s + 8.854)))
    }

    @Test fun the_whole_clip() {
        check("whole", listOf(ExportSpan(media.startSeconds, media.endSeconds)))
    }

    @Test fun three_cuts_sharing_chunks() {
        val s = media.startSeconds
        check("cuts", listOf(ExportSpan(s + 0.5, s + 1.7), ExportSpan(s + 2.3, s + 3.1), ExportSpan(s + 6.2, s + 8.9)))
    }

    @Test fun a_sync_shift_keeps_the_picture() {
        val s = media.startSeconds
        check("late", listOf(ExportSpan(s + 2.0, s + 7.0)), 0.5)
        check("early", listOf(ExportSpan(s + 2.0, s + 7.0)), -0.3)
    }

    @Test fun chapters_are_readable() {
        val out = File(work, "chapters.mp4")
        val s = media.startSeconds
        LosslessExporter.export(media, listOf(ExportSpan(s + 1, s + 5)), 0.0, listOf(ExportChapter(s + 2, "Kill"), ExportChapter(s + 3, "Death")), out)
        val titles = run("ffprobe", "-v", "error", "-show_chapters", "-of", "csv=p=0", out.path)
        println("chapters: ${titles.size}")
        assertEquals(3, titles.size)
        assertTrue(titles[1].contains("Kill"))
    }
}
