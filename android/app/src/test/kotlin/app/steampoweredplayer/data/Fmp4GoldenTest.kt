// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import app.steampoweredplayer.Golden
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import org.junit.Assert.assertEquals
import org.junit.Test

/** `fmp4-cases.json` holds what the desktop's C# code returned for these exact bytes and values. */
class Fmp4GoldenTest {
    private val root = Json.parseToJsonElement(Golden.text("fmp4-cases.json")).jsonObject

    private fun cases(name: String) = (root.getValue(name) as JsonArray).map { it.jsonObject }
    private fun JsonObject.str(name: String) = get(name)?.jsonPrimitive?.contentOrNull
    private fun hex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    @Test fun head_checks() {
        for (c in cases("head")) assertEquals(c.str("name"), c.str("result"), ChunkHeads.classify(hex(c.str("hex")!!)).name)
    }

    @Test fun first_fragments() {
        for (c in cases("fragments")) {
            val bytes = hex(c.str("hex")!!)
            val r = FirstFragment.find(bytes)
            assertEquals(c.str("name"), c.str("status"), r.status.name)
            assertEquals(c.str("name"), c.getValue("offset").jsonPrimitive.long, r.offset)
            assertEquals(c.str("name"), c.getValue("length").jsonPrimitive.long, r.length)
            assertEquals(c.str("name"), c.getValue("requiredBytes").jsonPrimitive.long, r.requiredBytes)
            assertEquals(c.str("name"), c.str("tfdt"), FirstFragment.baseMediaDecodeTime(bytes)?.toString())
        }
    }

    @Test fun timescales() {
        for (c in cases("inits")) assertEquals(c.str("name"), c.str("timescale"), FirstFragment.timescale(hex(c.str("hex")!!))?.toString())
    }

    @Test fun tfdt_seconds() {
        for (c in cases("tfdts")) {
            val expected = c.str("seconds")?.toDouble()
            assertEquals(c.str("name"), expected, FirstFragment.tfdtSeconds(hex(c.str("init")!!), hex(c.str("head")!!)))
        }
    }

    @Test fun axis_shifts() {
        for (c in cases("axisShift")) {
            val t = (c.getValue("tfdtSeconds") as JsonPrimitive).content.toDouble()
            val shift = SessionMedia.axisShiftFor(t, c.getValue("chunk").jsonPrimitive.int, c.getValue("segmentSeconds").jsonPrimitive.content.toDouble())
            assertEquals("$t", c.getValue("shift").jsonPrimitive.content.toDouble(), shift, 0.0)
        }
    }

    @Test fun complete_chunks_need_both_streams_and_valid_heads() {
        val names = listOf(
            "chunk-stream0-00001.m4s", "chunk-stream1-00001.m4s",              // fine
            "chunk-stream0-00002.m4s", "chunk-stream1-00002.m4s.tmp",           // audio being written
            "chunk-stream0-00003.m4s", "chunk-stream1-00003.m4s",               // zero-filled video
            "chunk-stream1-00004.m4s",                                          // no video
            "chunk-stream0-00005.m4s", "chunk-stream1-00005.m4s", "session.mpd", "init-stream0.m4s",
        )
        val complete = ChunkScan.complete(names) { if (it == "chunk-stream0-00003.m4s") HeadCheck.ZeroFilled else HeadCheck.Valid }
        assertEquals(listOf(1, 5), complete)
        assertEquals(listOf(SegmentRun(1, 1), SegmentRun(5, 5)), SessionMedia.runsOf(complete))
    }
}
