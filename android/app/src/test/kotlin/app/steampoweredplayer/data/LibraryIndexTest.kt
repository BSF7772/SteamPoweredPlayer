// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import app.steampoweredplayer.Golden
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class LibraryIndexTest {
    @Test fun golden_v1_parses_every_field() {
        val parsed = LibraryIndex.parse(Golden.bytes("library-v1.json"))
        assertEquals(LibraryIndexStatus.Ok, parsed.status)
        val index = parsed.index!!
        assertEquals("11111111-2222-3333-4444-555555555555", index.libraryId)
        assertEquals(Instant.parse("2025-06-01T08:30:00Z"), index.generatedUtc)
        assertEquals(GameEntry("730", "Test Game", false, GameArt("art/730/cover.jpg", "art/730/hero.jpg", "art/730/heroBlur.jpg", "art/730/logo.png")), index.games[0])
        assertEquals(GameEntry("10307921510433554432", "Test Shortcut éテ", true), index.games[1])

        assertEquals(3, index.recordings.size)
        val cache = index.recordings[0]
        assertEquals(RecordingKind.CacheSession, cache.kind)
        assertEquals(listOf(SegmentRun(1, 40), SegmentRun(45, 60)), cache.runs)
        assertEquals(171.0, cache.durationSeconds, 0.0)
        assertEquals(true, cache.live)
        assertEquals(123456789L, cache.sizeBytes)
        val clip = index.recordings[1]
        assertEquals(RecordingKind.Clip, clip.kind)
        assertEquals("Test clip", clip.title)
        assertEquals(1250L, clip.exactOffsetMs)
        val downloaded = index.recordings[2]
        assertEquals(RecordingKind.DownloadedClip, downloaded.kind)
        assertNull(downloaded.gameId)
    }

    @Test fun unknown_fields_from_a_newer_writer_are_ignored() {
        val parsed = LibraryIndex.parse(Golden.bytes("library-v1-extra-fields.json"))
        assertEquals(LibraryIndexStatus.Ok, parsed.status)
        assertEquals("Test Game", parsed.index!!.games[0].name)
        assertEquals(SegmentRun(1, 5), parsed.index!!.recordings[0].runs[0])
        assertEquals(3.0, parsed.index!!.recordings[0].segmentSeconds, 0.0)
    }

    @Test fun higher_schema_version_is_reported_not_crashed_on() {
        val parsed = LibraryIndex.parse(Golden.bytes("library-v1-newer-schema.json"))
        assertEquals(LibraryIndexStatus.NewerSchema, parsed.status)
        assertEquals(7, parsed.schemaVersion)
        assertNull(parsed.index)
    }

    @Test fun higher_schema_with_compatible_shape_still_yields_a_best_effort_index() {
        val json = Golden.text("library-v1.json").replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")
        val parsed = LibraryIndex.parse(json.toByteArray())
        assertEquals(LibraryIndexStatus.NewerSchema, parsed.status)
        assertNotNull(parsed.index)
    }

    @Test fun corrupt_input_reports_corrupt_and_never_throws() = listOf(
        "", "not json", "[]", "{}", """{"schemaVersion":0}""",
        """{"schemaVersion":1,"libraryId":"x","generatedUtc":"bad","games":[],"recordings":[]}""",
        """{"schemaVersion":1,"libraryId":"x","generatedUtc":"2025-01-01T00:00:00Z","games":[],"recordings":[{"kind":"???","startUtc":"2025-01-01T00:00:00Z","relativeFolder":"a","runs":[]}]}""",
        """{"schemaVersion":1,"libraryId":"x","generatedUtc":"2025-01-01T00:00:00Z","games":[],"recordings":[{"kind":"clip","startUtc":"2025-01-01T00:00:00Z","relativeFolder":"a","runs":[[5,1]]}]}""",
    ).forEach {
        val parsed = LibraryIndex.parse(it.toByteArray())
        assertEquals(it, LibraryIndexStatus.Corrupt, parsed.status)
        assertNull(it, parsed.index)
    }
}
