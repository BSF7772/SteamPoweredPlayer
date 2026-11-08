// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import app.steampoweredplayer.Golden
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

/** The cases of the desktop's ClipEndTests and TimelineTests. */
class ClipBoundsAndTimelineTest {
    private val shift = 1533.0

    private fun mpd(start: String = "PT25M34.317S", duration: String = "PT10.092S", type: String = "static") =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"$type\" mediaPresentationDuration=\"$duration\">\n" +
            "    <Period id=\"0\" start=\"$start\">\n        <AdaptationSet id=\"0\" contentType=\"video\"/>\n    </Period>\n</MPD>"

    private fun clip() = SessionMedia("x", listOf(SegmentRun(1, 4)), 3.0, shift)

    @Test fun start_and_end_come_from_the_manifest() {
        val m = ClipBounds.apply(clip(), mpd())
        assertEquals(1534.317, m.startSeconds, 1e-6)
        assertEquals(1544.409, m.endSeconds, 1e-6)
        assertEquals(1533.0, m.chunkStartSeconds(1), 0.0)
    }

    @Test fun a_start_that_does_not_fit_the_first_chunk_is_ignored() {
        for (start in listOf("PT25M33S", "PT25M0S", "PT25M40S")) {
            assertNull(start, ClipBounds.apply(clip(), mpd(start = start, duration = "PT9S")).startLimitSeconds)
        }
    }

    @Test fun a_live_manifest_has_no_limits() {
        assertNull(ClipBounds.startFromManifest(mpd(type = "dynamic"), shift, 3.0))
        assertNull(ClipBounds.fromManifest(mpd(type = "dynamic"), 1545.0, 3.0))
    }

    @Test fun durations_in_iso_8601_are_read() {
        assertEquals(3723.5, ClipBounds.parseDuration("PT1H2M3.5S")!!, 0.0)
        assertEquals(1534.317, ClipBounds.parseDuration("PT25M34.317S")!!, 1e-9)
        assertEquals(0.5, ClipBounds.parseDuration("PT0.5S")!!, 0.0)
        assertEquals(420.0, ClipBounds.parseDuration("PT7M")!!, 0.0)
    }

    @Test fun anything_else_is_not_a_duration() {
        for (text in listOf("", "10.092", "PT", "P1D", "PT-5S", "PTxS")) assertNull(text, ClipBounds.parseDuration(text))
        assertNull(ClipBounds.parseDuration(null))
    }

    @Test fun the_end_must_leave_less_than_a_chunk_of_tail() {
        assertNotNull(ClipBounds.fromManifest(mpd(), 1545.0, 3.0))
        assertNull(ClipBounds.fromManifest(mpd(), 1548.0, 3.0))
        assertNotNull(ClipBounds.fromManifest(mpd(), 1547.45, 3.0))
    }

    @Test fun an_end_at_or_past_the_last_chunk_is_not_a_trim() {
        assertNull(ClipBounds.fromManifest(mpd(), 1544.409, 3.0))
        assertNull(ClipBounds.fromManifest(mpd(), 1544.0, 3.0))
    }

    @Test fun unreadable_manifests_give_no_limits() {
        for (text in listOf(null, "", "\u0000\u0000\u0000\u0000", "<MPD type=\"static\"></MPD>")) {
            assertNull(ClipBounds.fromManifest(text, 1545.0, 3.0))
        }
        assertNull(ClipBounds.fromManifest(mpd().replace(" mediaPresentationDuration=\"PT10.092S\"", ""), 1545.0, 3.0))
        assertNull(ClipBounds.fromManifest(mpd().replace(" start=\"PT25M34.317S\"", ""), 1545.0, 3.0))
        assertNull(ClipBounds.fromManifest(mpd(duration = "PT0.0S"), 1545.0, 3.0))
    }

    @Test fun apply_leaves_media_without_limits_alone() {
        val media = clip()
        assertSame(media, ClipBounds.apply(media, null))
        val empty = SessionMedia("x", emptyList())
        assertSame(empty, ClipBounds.apply(empty, mpd()))
    }

    @Test fun the_lenient_timeline_reads_like_on_the_desktop() {
        val t = Timeline.parse(Golden.raw("../timelines/timeline-lenient.json"))
        assertEquals(Instant.parse("2025-01-01T13:00:00Z"), t.dateRecordedUtc)
        assertEquals(300000L, t.endTimeMs)
        assertEquals(listOf("17", "neg-duration", "bad-tag", "no-type"), t.entries.map { it.id })
        val first = t.entries[0]
        assertEquals(5000L, first.timeMs)
        assertEquals(2500L, first.durationMs)
        assertEquals(3L, first.priority)
        assertTrue(first.possibleClip)
        assertEquals(0L, t.entries[1].durationMs)
        assertEquals(listOf("ok"), t.entries[2].tags.map { it.name })
        assertEquals(TimelineEntryType.Unknown, t.entries[3].type)
        assertEquals(t.diagnostics.toString(), 5, t.diagnostics.size)
        assertTrue(t.diagnostics.any { "entries[1]" in it && "skipped" in it })
        assertTrue(t.diagnostics.any { "entries[2]" in it && "not an object" in it })
        assertTrue(t.diagnostics.any { "entries[3]" in it && "skipped" in it })
        assertTrue(t.diagnostics.any { "entries[5]" in it && "tag" in it })
    }

    @Test fun invalid_json_and_wrong_shapes_never_throw() {
        assertFalse(Timeline.parse("{ not json".toByteArray()).diagnostics.isEmpty())
        assertFalse(Timeline.parse("[1,2,3]".toByteArray()).diagnostics.isEmpty())
        assertFalse(Timeline.parse(ByteArray(0)).diagnostics.isEmpty())
        val r = Timeline.parse("""{"daterecorded":"abc","entries":{"a":1}}""".toByteArray())
        assertNull(r.dateRecordedUtc)
        assertEquals(2, r.diagnostics.size)
    }

    @Test fun empty_entries_written_as_an_object_are_no_problem() {
        val r = Timeline.parse("""{"entries":{}}""".toByteArray())
        assertTrue(r.entries.isEmpty())
        assertTrue(r.diagnostics.isEmpty())
    }

    @Test fun without_a_time_source_nothing_is_placed() {
        val set = Markers.map(Timeline.parse("""{"entries":[{"id":"a","time":1000}]}""".toByteArray()), null)
        assertEquals(MarkerAlignment.None, set.alignment)
        assertTrue(set.markers.isEmpty())
        assertEquals(1, set.diagnostics.size)
    }

    @Test fun markers_stop_at_a_clips_intended_limits() {
        val media = ClipBounds.apply(clip(), mpd())
        val timeline = Timeline.parse("""{"entries":[{"id":"before","time":1533500},{"id":"in","time":1540000},{"id":"after","time":1544900}]}""".toByteArray())
        val placed = Markers.place(Markers.map(timeline, 0).markers, media)
        assertEquals(listOf("in"), placed.visible.map { it.entry.id })
        assertEquals(TrimReason.BeforeStart, placed.trimmedAway.first { it.marker.entry.id == "before" }.reason)
        assertEquals(TrimReason.AfterEnd, placed.trimmedAway.first { it.marker.entry.id == "after" }.reason)
    }
}
