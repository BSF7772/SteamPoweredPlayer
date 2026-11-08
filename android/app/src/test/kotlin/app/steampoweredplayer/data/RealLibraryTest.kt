// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/** Opt-in and read-only: `SPP_REAL_LIBRARY` is a BackgroundRecordPath. Reports counts only: nothing real ends up in the output or the repo. */
class RealLibraryTest {
    private val clips: List<File> by lazy {
        val root = System.getenv("SPP_REAL_LIBRARY")
        assumeTrue("SPP_REAL_LIBRARY is not set", !root.isNullOrEmpty())
        File(root, "clips").listFiles().orEmpty().filter { it.isDirectory && it.name.startsWith("clip_") }
    }

    @Test fun every_clip_pb_parses_and_names_its_media_folder() {
        assumeTrue(clips.isNotEmpty())
        var problems = 0
        var withFolder = 0
        for (clip in clips) {
            val pb = File(clip, "clip.pb").takeIf { it.exists() } ?: continue
            val parsed = RecordingPb.parseClip(pb.readBytes())
            if (parsed.diagnostics.isNotEmpty()) problems++
            if (parsed.mediaFolders.any { File(clip, "video/$it").isDirectory }) withFolder++
        }
        println("real clips: ${clips.size}, with problems: $problems, naming a folder on disk: $withFolder")
        assertEquals(0, problems)
        assertTrue(withFolder > 0)
    }

    @Test fun nearly_every_clip_manifest_gives_an_end_within_the_last_chunk() {
        assumeTrue(clips.isNotEmpty())
        var total = 0
        var withEnd = 0
        for (clip in clips) {
            for (folder in File(clip, "video").listFiles().orEmpty()) {
                val mpd = File(folder, "session.mpd").takeIf { it.exists() } ?: continue
                val names = folder.list().orEmpty().toList()
                val numbers = names.mapNotNull { FolderNames.parseChunk(it) }.filter { !it.temporary && it.stream == 0 }.map { it.number }
                val last = numbers.maxOrNull() ?: continue
                val start = ClipBounds.parseDuration(Regex("""<Period[^>]*\sstart="([^"]+)"""").find(mpd.readText())?.groupValues?.get(1)) ?: continue
                // The axis shift, like the desktop's: a clip's chunks keep the session's tfdt, on a whole number of segments.
                val shift = Math.floor(start / SessionMedia.DEFAULT_SEGMENT_SECONDS) * SessionMedia.DEFAULT_SEGMENT_SECONDS
                val media = SessionMedia(folder.path, listOf(SegmentRun(numbers.min(), last)), axisShiftSeconds = shift)
                total++
                if (ClipBounds.fromManifest(mpd.readText(), media.chunkEndSeconds(last), media.segmentSeconds) != null) withEnd++
            }
        }
        println("real clip manifests: $total, with an end inside the last chunk: $withEnd")
        assertTrue(total > 0)
        assertTrue("$withEnd of $total", withEnd * 10 >= total * 9)
    }
}
