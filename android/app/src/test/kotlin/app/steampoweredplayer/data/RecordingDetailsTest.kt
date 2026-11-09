// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.File
import java.nio.ByteBuffer
import java.nio.file.Files
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RecordingDetailsTest {
    private val dir: File = Files.createTempDirectory("spp-details").toFile()

    private fun box(type: String, vararg body: ByteArray): ByteArray {
        val size = body.sumOf { it.size } + 8
        return ByteBuffer.allocate(size).putInt(size).put(type.toByteArray()).also { b -> body.forEach(b::put) }.array()
    }

    private fun int(v: Int) = ByteBuffer.allocate(4).putInt(v).array()
    private fun long(v: Long) = ByteBuffer.allocate(8).putLong(v).array()

    private fun init(timescale: Int) = box("moov", box("trak", box("mdia", box("mdhd", int(0), int(0), int(0), int(timescale), int(0), int(0)))))

    private fun chunk(tfdt: Long) = box("moof", box("traf", box("tfdt", int(0x01000000), long(tfdt)))) + box("mdat", ByteArray(4))

    private val layout = LibraryLayout("rec", "data")

    private fun clipEntry(runs: List<SegmentRun>) = RecordingEntry(
        WireKind.Clip, "730", Instant.parse("2026-01-01T00:00:00Z"), "clips/clip_730_20260101_000000", runs, 3.0, 12.0,
    )

    private fun share() = FakeShare().apply {
        val media = "rec/clips/clip_730_20260101_000000/video/bg_730_20251231_235000"
        put("$media/init-stream0.m4s", init(1_000_000))
        for (n in 1..4) {
            put("$media/chunk-stream0-0000$n.m4s", chunk(600_018_000L + (n - 1) * 3_000_000))
            put("$media/chunk-stream1-0000$n.m4s", chunk(0))
        }
        put(
            "$media/session.mpd",
            """<MPD type="static" mediaPresentationDuration="PT7.5S"><Period start="PT601.5S"></Period></MPD>""".toByteArray(),
        )
    }

    @Test fun a_clip_gets_its_axis_shift_from_the_chunks_own_tfdt_and_its_bounds_from_the_manifest() {
        val details = RecordingRepository(dir, share(), layout).load(clipEntry(listOf(SegmentRun(1, 4))), checkHeads = false)
        assertEquals(600.0, details.axisShiftSeconds, 0.0)
        assertEquals(601.5, details.startLimitSeconds!!, 1e-9)
        assertEquals(609.0, details.endLimitSeconds!!, 1e-9)
        val media = details.media("/local")
        assertEquals(601.5, media.startSeconds, 1e-9)
        assertEquals(609.0, media.endSeconds, 1e-9)
        assertEquals(7.5, media.durationSeconds, 1e-9)
    }

    @Test fun the_runs_are_what_the_index_says_and_is_still_there() {
        val share = share()
        share.content.remove("rec/clips/clip_730_20260101_000000/video/bg_730_20251231_235000/chunk-stream0-00004.m4s")
        val details = RecordingRepository(dir, share, layout).load(clipEntry(listOf(SegmentRun(1, 4))), checkHeads = false)
        assertEquals(listOf(SegmentRun(1, 3)), details.runs)
    }

    @Test fun without_the_desktops_index_a_zero_filled_chunk_is_left_out() {
        val share = share()
        val media = "rec/clips/clip_730_20260101_000000/video/bg_730_20251231_235000"
        share.put("$media/chunk-stream0-00002.m4s", ByteArray(64))
        val details = RecordingRepository(dir, share, layout).load(clipEntry(listOf(SegmentRun(1, 4))), checkHeads = true)
        assertEquals(listOf(SegmentRun(1, 1), SegmentRun(3, 4)), details.runs)
    }

    @Test fun markers_and_trickplay_stay_on_the_phone_for_offline() {
        val share = share()
        val clip = "rec/clips/clip_730_20260101_000000"
        share.put("$clip/timelines/timeline_730.json", """{"daterecorded":1767225000,"entries":[{"time":"3000","type":"event","title":"x"}]}""".toByteArray())
        share.put("data/trick/clip_730_20260101_000000/manifest.json", """{"version":1,"tileWidth":160,"tileHeight":90,"columns":10,"rows":10,"intervalSeconds":3,"sheets":[{"file":"sheet-0000.jpg","chunks":[1,2]}],"missing":[]}""".toByteArray())
        share.put("data/trick/clip_730_20260101_000000/sheet-0000.jpg", ByteArray(10))
        val repo = RecordingRepository(dir, share, layout)
        val loaded = repo.load(clipEntry(listOf(SegmentRun(1, 4))), checkHeads = false)
        assertTrue(loaded.hasTimeline)
        assertTrue(loaded.hasTrickplay)
        share.offline = true
        val offline = RecordingRepository(dir, null, null)
        val stored = offline.stored(loaded.key)!!
        assertEquals(1, offline.markers(stored).markers.size)
        val trick = offline.trickplay(stored.key)
        assertNotNull(trick)
        assertTrue(File(offline.trickDir(stored.key), "sheet-0000.jpg").exists())
        assertEquals(TrickplayTile(2, "sheet-0000.jpg", 160, 0, 160, 90), TrickplayIndex(trick!!, 600.0).findChunk(2))
        assertEquals(2, TrickplayIndex(trick, 600.0).findNearest(604.0)!!.chunk)
    }

    @Test fun a_session_finds_its_timeline_by_the_game_and_the_start_time() {
        val share = FakeShare().apply {
            put("rec/video/bg_730_20260101_100000/chunk-stream0-00001.m4s", chunk(0))
            put("rec/video/bg_730_20260101_100000/chunk-stream1-00001.m4s", chunk(0))
            put("rec/video/bg_730_20260101_100000/init-stream0.m4s", init(1000))
            put("rec/timelines/timeline_73020260101_095800.json", """{"daterecorded":1767261480,"entries":[{"time":180000,"type":"event"}]}""".toByteArray())
            put("rec/timelines/timeline_73020260101_110000.json", "{}".toByteArray())
            put("rec/timelines/timeline_44020260101_095900.json", "{}".toByteArray())
        }
        val entry = RecordingEntry(WireKind.Cache, "730", Instant.parse("2026-01-01T10:00:00Z"), "video/bg_730_20260101_100000", listOf(SegmentRun(1, 1)), temporary = true)
        val repo = RecordingRepository(dir, share, layout)
        val details = repo.load(entry, checkHeads = false)
        assertFalse(details.hasTrickplay)
        val set = repo.markers(details)
        assertEquals(MarkerAlignment.Approximate, set.alignment)
        assertEquals(60.0, set.markers.single().startSeconds, 1e-9)                   // the file's own start is 2 minutes before the session's, 3 minutes in
    }
}
