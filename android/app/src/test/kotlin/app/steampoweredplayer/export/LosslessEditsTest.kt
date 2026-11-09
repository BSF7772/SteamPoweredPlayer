// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import app.steampoweredplayer.data.SegmentRun
import app.steampoweredplayer.data.SessionMedia
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** The same cases as the desktop's `Mp4EditListTests`. */
class LosslessEditsTest {
    private val scales = Mp4Timescales(6_000_000, 1_000_000, 48_000)

    private fun video(vararg source: Double) = StoredPackets(source, LongArray(source.size) { Math.round((source[it] - 100) * 1_000_000) })

    private fun audio(from: Double, count: Int): StoredPackets {
        val source = DoubleArray(count) { from + it * 1024.0 / 48000 }
        return StoredPackets(source, LongArray(count) { Math.round((source[it] - from) * 48000) })
    }

    private fun steady(count: Int) = video(*DoubleArray(count) { 100 + it * 0.01 })

    @Test fun one_range_shows_the_frames_from_a_up_to_b_and_stops_before_the_next_frame() {
        val v = video(100.00, 100.03, 100.01, 100.02, 100.06, 100.04, 100.05)
        val (pv, pa) = LosslessEdits.compute(listOf(LosslessRange(100.015, 100.045)), v, audio(100.0, 50), scales, 0.0)
        val e = pv.single()
        assertEquals(20_000, e.mediaTime)                                             // 100.02, the first frame past A
        assertEquals((100.05 - 100.02) * 6_000_000 - 1, e.duration.toDouble(), 1.0)   // up to 100.05, which isn't shown
        assertEquals(e.duration, pa.single().duration)
    }

    @Test fun a_frame_stored_after_b_is_hidden_though_it_decodes_before_frames_shown() {
        val v = video(100.00, 100.03, 100.01, 100.02)
        val e = LosslessEdits.compute(listOf(LosslessRange(100.00, 100.025)), v, audio(100.0, 50), scales, 0.0).first.single()
        assertTrue(e.mediaTime + e.duration * 1_000_000 / 6_000_000 < 30_000)
    }

    @Test fun every_range_gets_its_own_entry_and_the_sound_follows_each() {
        val (v, a) = LosslessEdits.compute(listOf(LosslessRange(100.5, 100.8), LosslessRange(101.0, 101.1), LosslessRange(102.0, 102.5)), steady(300), audio(100.0, 400), scales, 0.0)
        assertEquals(listOf(500_000L, 1_000_000L, 2_000_000L), v.map { it.mediaTime })
        assertEquals(listOf(24_000L, 48_000L, 96_000L), a.map { it.mediaTime })
        v.zip(a).forEach { (picture, sound) -> assertTrue(sound.duration - picture.duration in 0..1) }
    }

    @Test fun a_sync_shift_moves_the_sounds_start() {
        for ((delay, source) in listOf(0.3 to 100.2, -0.5 to 101.0)) {
            val a = LosslessEdits.compute(listOf(LosslessRange(100.5, 101.0)), steady(300), audio(100.0, 400), scales, delay).second
            assertEquals(Math.round((source - 100) * 48000), a.single().mediaTime)
        }
    }

    @Test fun sound_missing_at_the_recordings_head_is_a_stretch_of_nothing() {
        val (v, a) = LosslessEdits.compute(listOf(LosslessRange(100.0, 100.9)), steady(100), audio(100.0, 100), scales, 0.5)
        assertEquals(2, a.size)
        assertEquals(EditEntry(3_000_000, -1), a[0])
        assertEquals(0, a[1].mediaTime)
        assertTrue(a.sumOf { it.duration } - v[0].duration in 0..1)
    }

    @Test fun sound_missing_between_ranges_keeps_the_next_range_on_its_picture() {
        val (v, a) = LosslessEdits.compute(listOf(LosslessRange(100.0, 100.5), LosslessRange(102.0, 102.5)), steady(300), audio(100.0, 12), scales, 0.0)
        assertEquals(-1, a[1].mediaTime)
        assertEquals(v[0].duration, a[0].duration + a[1].duration)
    }

    @Test fun an_open_tail_keeps_a_last_frame_a_few_ms_past_b() {
        val v = video(100.00, 100.01, 100.02)
        val open = LosslessEdits.compute(listOf(LosslessRange(100.0, 100.015, openTail = true)), v, audio(100.0, 50), scales, 0.0).first
        val strict = LosslessEdits.compute(listOf(LosslessRange(100.0, 100.015)), v, audio(100.0, 50), scales, 0.0).first
        assertTrue(open[0].duration > strict[0].duration + 10_000)
    }

    @Test fun a_different_packet_count_is_refused_rather_than_guessed() {
        val bad = StoredPackets(doubleArrayOf(1.0, 2.0), longArrayOf(1))
        assertThrows(IllegalStateException::class.java) {
            LosslessEdits.compute(listOf(LosslessRange(0.0, 3.0)), bad, audio(0.0, 5), scales, 0.0)
        }
    }

    @Test fun open_ends_apply_only_at_the_recordings_own_first_and_last_chunk() {
        val media = SessionMedia("/m", listOf(SegmentRun(1, 10)))
        val whole = ExportPlan.whole(media)!!
        assertEquals(LosslessRange(0.0, 30.0, openHead = true, openTail = true), LosslessEdits.rangeOf(media, whole))
        val inner = ExportPlan.of(media, 6.0, 12.0)!!
        assertEquals(LosslessRange(6.0, 12.0), LosslessEdits.rangeOf(media, inner))
        val clip = media.copy(startLimitSeconds = 1.0, endLimitSeconds = 28.0)
        assertEquals(LosslessRange(1.0, 28.0), LosslessEdits.rangeOf(clip, ExportPlan.whole(clip)!!))
    }

    @Test fun plans_keep_every_range_and_chunks_are_stored_once() {
        val media = SessionMedia("/m", listOf(SegmentRun(1, 10)))
        val plans = ExportSpans.plans(media, listOf(ExportSpan(3.5, 4.0), ExportSpan(4.5, 5.0), ExportSpan(9.5, 10.0)))
        assertEquals(3, plans.size)
        val (video, audio) = LosslessExporter.chunksNeeded(media, plans, 0.0)
        assertEquals(listOf(2, 4), video)
        assertEquals(listOf(1, 2, 3, 4, 5), audio)
    }

    @Test fun a_sync_shift_reads_sound_past_the_pictures_chunks() {
        val media = SessionMedia("/m", listOf(SegmentRun(1, 10)))
        val plan = ExportPlan.of(media, 6.0, 9.0)!!
        assertEquals(1 to 3, ExportPlan.audioChunks(media, plan, 3.0))                // delayed: earlier sound
        assertEquals(3 to 5, ExportPlan.audioChunks(media, plan, -3.0))               // earlier: later sound
    }

    @Test fun a_picture_well_short_of_the_kept_length_is_not_whole() {
        assertTrue(ExportSpans.isWholePicture(9.6, 10.0))                             // half a second is allowed
        assertTrue(!ExportSpans.isWholePicture(9.4, 10.0))
        assertTrue(ExportSpans.isWholePicture(95.5, 100.0))                           // 5 % once that's more
        assertTrue(!ExportSpans.isWholePicture(94.0, 100.0))
        assertTrue(!ExportSpans.isWholePicture(0.0, 2.0))
    }

    @Test fun chapters_follow_the_cuts_and_start_at_zero() {
        val media = SessionMedia("/m", listOf(SegmentRun(1, 10)))
        val plans = ExportSpans.plans(media, listOf(ExportSpan(3.0, 6.0), ExportSpan(9.0, 12.0)))
        val laid = ExportChapters.layout(plans, listOf(ExportChapter(4.0, "a"), ExportChapter(7.0, "cut away"), ExportChapter(10.0, "b")), 1.0)
        assertEquals(listOf(OutputChapter(0.0, 1.0, "Start"), OutputChapter(1.0, 4.0, "a"), OutputChapter(4.0, 6.0, "b")), laid)
    }
}
