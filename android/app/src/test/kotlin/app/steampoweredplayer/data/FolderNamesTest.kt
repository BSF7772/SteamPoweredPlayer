// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FolderNamesTest {
    @Test fun background_session_parses_as_utc_cache_session() =
        assertEquals(RecordingName(RecordingKind.CacheSession, 730u, Instant.parse("2025-01-01T12:00:00Z")), FolderNames.parseRecording("bg_730_20250101_120000"))

    @Test fun clip_with_shortcut_game_id_parses() =
        assertEquals(10307921510433554432u, FolderNames.parseRecording("clip_10307921510433554432_20251231_235959")?.gameId)

    @Test fun downloaded_clip_has_no_game_id() {
        val r = FolderNames.parseRecording("clip_downloaded_20250102_030405")!!
        assertEquals(RecordingKind.DownloadedClip, r.kind)
        assertNull(r.gameId)
    }

    @Test fun unknown_layouts_return_null() = listOf(
        "bg_730_20250101_1200", "bg_730_20251301_120000", "bg_730_20250230_120000", "bg_730_20250101_120000_x", "rec_730_20250101_120000",
        "bg_99999999999999999999999_20250101_120000", "BG_730_20250101_120000", "",
    ).forEach { assertNull(it, FolderNames.parseRecording(it)) }

    @Test fun timeline_without_separator_splits_game_id_and_date() {
        val t = FolderNames.parseTimeline("timeline_73020250101_120000.json")!!
        assertEquals(730uL, t.gameId)
        assertEquals(Instant.parse("2025-01-01T12:00:00Z"), t.startUtc)
        assertNull(FolderNames.parseTimeline("timeline_730_20250101_120000.json"))
    }

    @Test fun chunk_names_parse() {
        assertEquals(ChunkName(0, 1840, false), FolderNames.parseChunk("chunk-stream0-01840.m4s"))
        assertEquals(ChunkName(0, 123456, false), FolderNames.parseChunk("chunk-stream0-123456.m4s"))
        assertEquals(ChunkName(1, 7, true), FolderNames.parseChunk("chunk-stream1-00007.m4s.tmp"))
        listOf("chunk-stream2-00001.m4s", "init-stream0.m4s", "chunk-stream0-00001.mp4").forEach { assertNull(it, FolderNames.parseChunk(it)) }
    }

    @Test fun clip_media_folders_parse() {
        assertEquals(null, FolderNames.parseMediaFolder("bg_730_20250101_120000")?.index)
        assertEquals(0, FolderNames.parseMediaFolder("bg_730_20250101_120000_0")?.index)
        assertEquals(true, FolderNames.parseMediaFolder("fg_730_20250101_120000")?.foreground)
        listOf("clip_730_20250101_120000", "bg_730_20250101_120000_", "xg_730_20250101_120000").forEach { assertNull(it, FolderNames.parseMediaFolder(it)) }
    }

    @Test fun chunk_file_round_trips() = assertEquals("chunk-stream0-01840.m4s", FolderNames.chunkFile(0, 1840))
}
