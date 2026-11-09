// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ExportLadderTest {
    private val hwHevc = EncoderCap("hw.hevc", CodecMime.HEVC, true, 4096, 4096)
    private val hwAvc = EncoderCap("hw.avc", CodecMime.AVC, true, 4096, 4096)
    private val hwAv1 = EncoderCap("hw.av1", CodecMime.AV1, true, 4096, 4096)
    private val swHevc1080 = EncoderCap("sw.hevc", CodecMime.HEVC, false, 1920, 1920)
    private val swAvc = EncoderCap("sw.avc", CodecMime.AVC, false, 2048, 2048)
    private val swAv1 = EncoderCap("sw.av1", CodecMime.AV1, false, 4096, 4096)
    private val phone = listOf(hwHevc, hwAvc, swHevc1080, swAvc)

    private fun plan(id: String, engine: LadderEngine = LadderEngine.Auto, encoders: List<EncoderCap> = phone, target: Double? = null) =
        ExportLadder.plan(ExportLadder.tier(id)!!, engine, encoders, 2560, 1440, 60.0, 10.0, target)

    @Test fun the_tiers_are_the_approved_ones_with_their_file_names() {
        assertEquals(listOf("ultra-quality", "quality", "medium", "performance", "ultra-performance"), ExportLadder.tiers.map { it.id })
        assertEquals("_M", ExportLadder.fileSuffix("medium"))
        assertEquals("_UP", ExportLadder.fileSuffix("ultra-performance"))
        assertEquals("", ExportLadder.fileSuffix("archive"))
        assertEquals("", ExportLadder.fileSuffix(ExportLadder.SAVE_CLIP_ID))
        assertEquals("medium", ExportLadder.DEFAULT_ID)
    }

    @Test fun bitrates_fall_from_quality_to_speed() {
        val rates = listOf("ultra-quality", "quality", "medium", "performance").map { plan(it)!!.videoBitsPerSecond }
        assertEquals(rates.sortedDescending(), rates)
    }

    @Test fun ultra_performance_is_720p30_h264() {
        val p = plan("ultra-performance")!!
        assertEquals(CodecMime.AVC, p.mime)
        assertEquals(1280 to 720, p.width to p.height)
        assertEquals(30f, p.fpsCap)
        assertTrue(p.videoBitsPerSecond < 4_000_000)
    }

    @Test fun av1_is_used_only_with_a_hardware_encoder_on_auto() {
        assertEquals(CodecMime.HEVC, plan("medium", encoders = phone + swAv1)!!.mime)
        assertEquals(CodecMime.AV1, plan("medium", encoders = phone + hwAv1)!!.mime)
    }

    @Test fun the_engine_switch_is_respected() {
        assertEquals("hw.hevc", plan("medium", LadderEngine.Gpu)!!.encoder)
        assertEquals("sw.avc", plan("ultra-performance", LadderEngine.Cpu)!!.encoder)
    }

    @Test fun an_encoder_that_refuses_the_size_gets_a_smaller_picture_when_nothing_takes_it() {
        val cpu = plan("medium", LadderEngine.Cpu)!!                                  // no software encoder here takes 1440p
        assertEquals("sw.hevc", cpu.encoder)
        assertEquals(1920 to 1080, cpu.width to cpu.height)
        assertEquals(1920 to 1080, plan("medium", encoders = listOf(swHevc1080, swAvc))!!.let { it.width to it.height })
        assertNull(plan("medium", encoders = listOf(EncoderCap("tiny", CodecMime.HEVC, false, 512, 512))))
    }

    @Test fun ultra_quality_prefers_software_and_falls_back_when_the_phone_has_none_for_this_size() {
        assertEquals("hw.hevc", plan("ultra-quality")!!.encoder)
        val big = listOf(hwHevc, EncoderCap("sw.big", CodecMime.HEVC, false, 4096, 4096))
        assertEquals("sw.big", plan("ultra-quality", encoders = big)!!.encoder)
    }

    @Test fun no_encoder_means_no_plan() {
        assertNull(plan("medium", encoders = emptyList()))
        assertNotNull(plan("medium", encoders = listOf(hwAvc)))
    }

    @Test fun fit_to_size_turns_the_budget_into_a_bitrate() {
        val p = plan(ExportLadder.TARGET_ID, target = 10.0)!!
        val bytes = ExportLadder.estimateBytes(p, 10.0)
        assertTrue("$bytes", bytes in (9L * 1024 * 1024)..(10L * 1024 * 1024))
    }

    @Test fun scaling_keeps_the_shape_on_even_numbers() {
        assertEquals(1280 to 720, ExportLadder.scaled(2560, 1440, 720))
        assertEquals(1720 to 720, ExportLadder.scaled(3440, 1440, 720))
        assertEquals(640 to 360, ExportLadder.scaled(640, 360, 720))
    }
}
