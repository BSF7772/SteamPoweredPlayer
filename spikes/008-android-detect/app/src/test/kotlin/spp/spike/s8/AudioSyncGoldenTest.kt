// SPDX-License-Identifier: GPL-3.0-or-later
package spp.spike.s8

import org.junit.Assert.assertEquals
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File

/**
 * Port check against C#: the inputs are the exact ffmpeg outputs the C# detector consumes (tools/dump-inputs.sh), the
 * expected values are what FastEncoder's AudioSync.DetectAsync returned on the same file (golden.json, from golden-cs/).
 * Set -Ps8.golden=<dir with one sub-folder per fixture> (or env S8_GOLDEN); skipped otherwise.
 */
class AudioSyncGoldenTest {
    private val root = (System.getProperty("s8.golden") ?: System.getenv("S8_GOLDEN"))?.let(::File)

    @Test
    fun matchesCSharpOnEveryFixture() {
        assumeTrue("S8_GOLDEN not set", root != null && root.isDirectory)
        val dirs = root!!.listFiles()!!.filter { File(it, "golden.json").exists() }.sortedBy { it.name }
        assumeTrue("no fixtures", dirs.isNotEmpty())
        for (d in dirs) {
            val g = org.json.JSONObject(File(d, "golden.json").readText())
            val audio = RawInputs.audio(File(d, "audio.f32"))
            val video = RawInputs.video(File(d, "video.gray"), File(d, "video.pts"))
            val t0 = System.nanoTime()
            val r = AudioSync.analyse(audio, video)
            val ms = (System.nanoTime() - t0) / 1e6
            println("${d.name}: kotlin ${"%.3f".format(r.audioLate * 1000)} ms ${r.confidence} ${r.agreeing}/${r.consistent} " +
                "| c# ${"%.3f".format(g.getDouble("audio_late") * 1000)} ms ${g.getString("confidence")} ${g.getInt("agreeing")}/${g.getInt("consistent")}" +
                " | analyse ${"%.0f".format(ms)} ms")
            assertEquals(d.name, g.getString("confidence"), r.confidence.name)
            assertEquals(d.name, g.getInt("agreeing"), r.agreeing)
            assertEquals(d.name, g.getInt("consistent"), r.consistent)
            assertEquals(d.name, g.getDouble("audio_late"), r.audioLate, 1e-6)
        }
    }
}
