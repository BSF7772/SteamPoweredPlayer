// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.edit

import kotlin.math.PI
import kotlin.math.sin
import kotlin.random.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The detector on synthetic inputs. Agreement with FastEncoder's C# on identical inputs was measured in spike S8; this code is that port. */
class AudioSyncTest {
    private val fps = 60

    /** Bursts of sound and flashes of picture at the same irregular moments, the sound [lateSeconds] after the picture. */
    private fun clip(lateSeconds: Double, seconds: Int = 20, seed: Int = 7): Pair<FloatArray, AudioSync.VideoFeatures> {
        val random = Random(seed)
        val moments = generateSequence(0.8) { it + 0.35 + random.nextDouble() * 0.9 }.takeWhile { it < seconds - 1 }.toList()
        val audio = FloatArray(seconds * AudioSync.SR)
        for (m in moments) {
            val start = ((m + lateSeconds) * AudioSync.SR).toInt()
            for (i in 0 until AudioSync.SR / 25) {
                if (start + i in audio.indices) audio[start + i] += (0.7 * sin(2 * PI * 3000 * i / AudioSync.SR) * (1 - i * 25.0 / AudioSync.SR)).toFloat()
            }
        }
        val acc = AudioSync.VideoAccumulator()
        for (f in 0 until seconds * fps) {
            val t = f.toDouble() / fps
            val lit = moments.any { t >= it && t < it + 0.05 }
            acc.add(ByteArray(AudioSync.W * AudioSync.H) { if (lit) 230.toByte() else 20 }, t)
        }
        return audio to acc.result()
    }

    @Test fun a_clear_late_sound_is_found_with_high_confidence_and_fixed() {
        val (audio, video) = clip(0.3)
        val r = AudioSync.analyse(audio, video)
        assertEquals(AudioSync.Confidence.High, r.confidence)
        assertEquals(0.3, r.audioLate, 0.035)                                         // FastEncoder's own self-test window
        val advice = SyncAdvice.from(r)
        assertEquals(SyncVerdict.Fix, advice.verdict)
        assertEquals(-r.audioLate, advice.audioDelay, 0.0)
    }

    @Test fun sound_in_step_with_the_picture_needs_nothing() {
        val (audio, video) = clip(0.0)
        val r = AudioSync.analyse(audio, video)
        assertEquals(AudioSync.Confidence.High, r.confidence)
        val advice = SyncAdvice.from(r)
        assertEquals(SyncVerdict.InSync, advice.verdict)
        assertEquals(0.0, advice.audioDelay, 0.0)
    }

    @Test fun a_low_confidence_result_never_yields_a_number() {
        for (confidence in listOf(AudioSync.Confidence.Low, AudioSync.Confidence.None)) {
            val advice = SyncAdvice.from(AudioSync.Result(0.5, confidence, 1, 12))
            assertEquals(SyncVerdict.NotApplied, advice.verdict)
            assertEquals(0.0, advice.audioDelay, 0.0)
            assertEquals(null, advice.audioLate)
        }
    }

    @Test fun too_little_to_go_on_is_none() {
        assertEquals(AudioSync.Confidence.None, AudioSync.analyse(FloatArray(100), AudioSync.VideoFeatures(DoubleArray(0), Array(4) { DoubleArray(0) })).confidence)
    }

    @Test fun in_sync_uses_the_desktops_window() {
        assertTrue(AudioSync.inSync(-0.04))
        assertTrue(AudioSync.inSync(0.07))
        assertTrue(!AudioSync.inSync(-0.05))
        assertTrue(!AudioSync.inSync(0.09))
    }

    @Test fun the_detector_listens_to_at_most_75_seconds_from_the_middle() {
        assertEquals(app.steampoweredplayer.export.ExportSpan(10.0, 20.0), SyncDetector.window(app.steampoweredplayer.export.ExportSpan(10.0, 20.0)))
        assertEquals(app.steampoweredplayer.export.ExportSpan(62.5, 137.5), SyncDetector.window(app.steampoweredplayer.export.ExportSpan(0.0, 200.0)))
    }
}
