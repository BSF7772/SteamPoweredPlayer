// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import app.steampoweredplayer.Golden
import app.steampoweredplayer.data.SegmentRun
import app.steampoweredplayer.data.SessionMedia
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.double
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class EdlGoldenTest {
    private val cases = Json.parseToJsonElement(Golden.text("edl-cases.json")).jsonObject

    private fun pairs(a: JsonArray) = a.map { p -> p.jsonArray.let { SegmentRun(it[0].jsonPrimitive.int, it[1].jsonPrimitive.int) } }
    private fun numbers(a: JsonArray) = pairs(a).flatMap { it.first..it.last }
    private fun shift(c: JsonObject) = c["axisShiftSeconds"]?.jsonPrimitive?.double ?: 0.0
    private fun str(c: JsonObject, k: String) = c.getValue(k).jsonPrimitive.content

    @Test fun every_golden_edl_matches_byte_for_byte() {
        val all = cases.getValue("cases").jsonArray
        assertEquals(9, all.size)
        for (e in all) {
            val c = e.jsonObject
            val chunks = numbers(c.getValue("chunks").jsonArray)
            val media = SessionMedia(str(c, "folder"), pairs(c.getValue("chunks").jsonArray), c.getValue("segmentSeconds").jsonPrimitive.double, shift(c))
            assertEquals(str(c, "name"), Golden.text(str(c, "expected")), Edl.build(media, chunks))
        }
    }

    @Test fun every_golden_window_matches() {
        val all = cases.getValue("windowCases").jsonArray
        assertEquals(8, all.size)
        for (e in all) {
            val c = e.jsonObject
            val media = SessionMedia("x", pairs(c.getValue("runs").jsonArray), 3.0, shift(c))
            val got = EdlWindow.window(media, c.getValue("position").jsonPrimitive.double, c.getValue("count").jsonPrimitive.int)
            assertEquals(str(c, "name"), numbers(c.getValue("expected").jsonArray), got)
        }
    }

    @Test fun bad_chunk_lists_are_refused() {
        val m = SessionMedia("x", listOf(SegmentRun(1, 5)))
        assertThrows(IllegalArgumentException::class.java) { Edl.build(m, emptyList()) }
        assertThrows(IllegalArgumentException::class.java) { Edl.build(m, listOf(3, 2)) }
        assertThrows(IllegalArgumentException::class.java) { Edl.build(m, listOf(2, 2)) }
    }

    @Test fun reload_reasons_follow_the_desktop() {
        val m = SessionMedia("x", listOf(SegmentRun(1, 200)))
        val w = (41..100).toList()
        assertEquals(ReloadReason.OutsideWindow, EdlWindow.shouldReload(w, m, 0.0, 0.0))
        assertEquals(ReloadReason.NearWindowEnd, EdlWindow.shouldReload(w, m, 285.0, 0.0))
        assertEquals(ReloadReason.None, EdlWindow.shouldReload(w, m, 150.0, 0.0))
        assertEquals(ReloadReason.Periodic, EdlWindow.shouldReload(w, m, 150.0, 600.0))
        assertEquals(ReloadReason.WindowStale, EdlWindow.shouldReload(w, SessionMedia("x", listOf(SegmentRun(60, 200))), 200.0, 0.0))
    }
}
