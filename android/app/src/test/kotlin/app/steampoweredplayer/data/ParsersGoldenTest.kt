// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import app.steampoweredplayer.Golden
import java.time.Instant
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.double
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The fixtures the C# tests use too, with the values they assert (tests/Steam). */
class ParsersGoldenTest {
    private fun gameRecording() = RecordingPb.parseGameRecording(Golden.raw("../pb/gamerecording-basic.pb"))

    @Test fun game_recording_reads_timelines_and_offsets() {
        val r = gameRecording()
        assertTrue(r.diagnostics.isEmpty())
        assertEquals(4, r.timelines.size)
        val t = r.timelines[0]
        assertEquals("1001", t.timelineId)
        assertEquals(730UL, t.gameId)
        assertEquals(1735732800L, t.dateRecordedUnix)
        assertEquals(600000L, t.durationMs)
        assertEquals("bg_730_20250101_120057", t.session!!.folder)
        assertEquals(57332L, r.offsetMsFor("1001"))
        assertEquals(57332L, r.offsetMsForFolder("bg_730_20250101_120057"))
    }

    @Test fun game_recording_keeps_later_recording_fields_and_missing_ones() {
        val r = gameRecording()
        val s = r.timelines[1].session!!
        assertEquals(130000L, s.startMs)
        assertEquals(200000L, s.durationMs)
        assertEquals(100000L, s.offsetMs)
        assertEquals(100000L, r.offsetMsFor("1002"))
        assertNull(r.timelines[2].session)
        assertNull(r.offsetMsFor("1003"))
        assertNull(r.offsetMsFor("does-not-exist"))
    }

    @Test fun game_recording_reads_ids_given_as_strings_or_past_the_signed_range() {
        val t = gameRecording().timelines[3]
        assertEquals("1004", t.timelineId)
        assertEquals(10307921510433554432UL, t.gameId)
        assertEquals(4000L, t.session!!.offsetMs)
    }

    @Test fun game_recording_reads_the_tag_index() {
        val e = gameRecording().tagIndex[0]
        assertEquals(730UL, e.appId)
        assertEquals(PbTag("de_made_up", "Map", "map", 1), e.tag)
        assertEquals(listOf(PbTagOccurrence("1001", 5000), PbTagOccurrence("1002", 6000)), e.occurrences)
    }

    @Test fun clip_reads_its_fields() {
        val c = RecordingPb.parseClip(Golden.raw("../pb/clip-basic.pb"))
        assertTrue(c.diagnostics.isEmpty())
        assertEquals("1001", c.timeline!!.timelineId)
        assertEquals(57332L, c.offsetMs)
        assertEquals("bg_730_20250101_120057", c.timeline!!.session!!.folder)
        assertEquals(listOf("bg_730_20250101_120057"), c.mediaFolders)
        assertEquals(30000L, c.timeline!!.session!!.durationMs)
        assertEquals(1735733000L, c.dateRecordedUnix)
        assertEquals(730UL, c.gameId)
        assertEquals("Made up clip 🎮", c.title)
        assertEquals(2560, c.thumbnailWidth)
        assertEquals(1440, c.thumbnailHeight)
    }

    @Test fun empty_and_cut_files_never_throw() {
        assertTrue(RecordingPb.parseGameRecording(ByteArray(0)).timelines.isEmpty())
        assertTrue(RecordingPb.parseClip(ByteArray(0)).diagnostics.isEmpty())
        val bytes = Golden.raw("../pb/gamerecording-basic.pb")
        val cut = RecordingPb.parseGameRecording(bytes.copyOf(bytes.size - 3))
        assertFalse(cut.diagnostics.isEmpty())
        assertFalse(RecordingPb.parseClip(ByteArray(64) { 0xFF.toByte() }).diagnostics.isEmpty())
    }

    @Test fun timeline_basic_has_no_problems() {
        val t = Timeline.parse(Golden.raw("../timelines/timeline-basic.json"))
        assertTrue(t.diagnostics.toString(), t.diagnostics.isEmpty())
        assertEquals(Instant.parse("2025-01-01T12:00:00Z"), t.dateRecordedUtc)
        assertTrue(t.entries.any { it.id == "e3" && it.durationMs == 8000L })
    }

    @Test fun markers_match_the_golden_results() {
        for (name in listOf("markers-exact.json", "markers-fallback.json", "markers-clip.json")) {
            val root = Json.parseToJsonElement(Golden.text(name)).jsonObject
            val timeline = Timeline.parse(Golden.raw(root.getValue("timeline").jsonPrimitive.content))
            assertTrue(name, timeline.diagnostics.isEmpty())

            val offset = root.getValue("offset").jsonObject
            fun file() = Golden.raw(offset.getValue("file").jsonPrimitive.content)
            val offsetMs = when (offset.getValue("source").jsonPrimitive.content) {
                "gamerecording" -> RecordingPb.parseGameRecording(file()).offsetMsFor(offset.getValue("timelineId").jsonPrimitive.content)
                "clip" -> RecordingPb.parseClip(file()).offsetMs
                else -> null
            }
            val bg = (root.getValue("bgStartUtc") as? JsonPrimitive)?.takeUnless { it is JsonNull }?.let { Instant.parse(it.content) }

            val mediaJson = root.getValue("media").jsonObject
            val runs = mediaJson.getValue("runs").jsonArray.map { r -> SegmentRun(r.jsonArray[0].jsonPrimitive.int, r.jsonArray[1].jsonPrimitive.int) }
            val media = SessionMedia("x", runs, mediaJson.getValue("segmentSeconds").jsonPrimitive.double)

            val set = Markers.map(timeline, offsetMs, bg)
            val placement = Markers.place(set.markers, media)

            val expected = root.getValue("expected").jsonObject
            assertEquals(name, expected.getValue("alignment").jsonPrimitive.content, set.alignment.name)
            assertMarkers(name, expected.getValue("visible").jsonArray, placement.visible.map { it to null })
            assertMarkers(name, expected.getValue("trimmed").jsonArray, placement.trimmedAway.map { it.marker to it.reason.name })
        }
    }

    private fun assertMarkers(name: String, expected: JsonArray, actual: List<Pair<Marker, String?>>) {
        assertEquals(name, expected.size, actual.size)
        expected.forEachIndexed { i, item ->
            val o: JsonObject = item.jsonObject
            val (marker, reason) = actual[i]
            assertEquals(name, o.getValue("id").jsonPrimitive.content, marker.entry.id)
            assertEquals(name, o.getValue("start").jsonPrimitive.double, marker.startSeconds, 1e-6)
            val end = o.getValue("end")
            if (end is JsonNull) assertNull(name, marker.endSeconds) else assertEquals(name, end.jsonPrimitive.double, marker.endSeconds!!, 1e-6)
            if (reason != null) assertEquals(name, o.getValue("reason").jsonPrimitive.content, reason)
        }
    }
}
