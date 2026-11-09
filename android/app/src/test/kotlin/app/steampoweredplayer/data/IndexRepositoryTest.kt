// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import app.steampoweredplayer.Golden
import java.io.ByteArrayInputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.nio.file.Files
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** A share in memory: path to bytes. Folders exist when a file is below them. */
class FakeShare : RemoteFiles {
    val content = HashMap<String, ByteArray>()
    var offline = false
    var failReads = 0
    var goneReads = 0
    val opened = ArrayList<String>()

    fun put(path: String, bytes: ByteArray = ByteArray(0)) {
        content[path] = bytes
    }

    override fun list(relativeFolder: String): List<String> {
        if (offline) throw IOException("offline")
        val prefix = if (relativeFolder.isEmpty()) "" else relativeFolder.trimEnd('/') + "/"
        val names = content.keys.filter { it.startsWith(prefix) }.map { it.removePrefix(prefix).substringBefore('/') }.distinct()
        if (names.isEmpty() && relativeFolder.isNotEmpty()) throw RemoteFileGone(relativeFolder)
        return names
    }

    override fun size(relativePath: String): Long = (content[relativePath] ?: throw RemoteFileGone(relativePath)).size.toLong()

    override fun open(relativePath: String, offset: Long): InputStream {
        if (offline) throw IOException("offline")
        opened.add(relativePath)
        if (failReads > 0) {
            failReads--
            throw IOException("sharing violation")
        }
        if (goneReads > 0) {
            goneReads--
            throw RemoteFileGone(relativePath)
        }
        val bytes = content[relativePath] ?: throw RemoteFileGone(relativePath)
        return ByteArrayInputStream(bytes, offset.toInt(), bytes.size - offset.toInt())
    }
}

class IndexRepositoryTest {
    private val dir: File = Files.createTempDirectory("spp-index").toFile()
    private val now = Instant.parse("2026-11-09T00:00:00Z")

    private fun repo(share: FakeShare, path: String = "") = IndexRepository(dir, share, path, { now }, {})

    private fun librarySetup(root: String = ""): FakeShare = FakeShare().apply {
        val base = if (root.isEmpty()) "" else "$root/"
        put("${base}Recordings/gamerecording.pb")
        put("${base}Recordings/video/bg_730_20250101_120000/chunk-stream0-00001.m4s")
        put("${base}SteamPoweredPlayer/library.json", Golden.bytes("library-v1.json"))
        put("${base}Other/readme.txt")
    }

    @Test fun finds_a_data_folder_next_to_the_recordings_from_the_share_root() {
        val layout = LibraryLocator.locate(librarySetup(), "")
        assertEquals(LibraryLayout("Recordings", "SteamPoweredPlayer"), layout)
    }

    @Test fun finds_them_when_the_profile_points_at_the_recordings_or_the_data_folder() {
        val share = librarySetup("media")
        assertEquals(LibraryLayout("media/Recordings", "media/SteamPoweredPlayer"), LibraryLocator.locate(share, "media/Recordings"))
        assertEquals(LibraryLayout("media/Recordings", "media/SteamPoweredPlayer"), LibraryLocator.locate(share, "media\\SteamPoweredPlayer\\"))
        assertEquals(LibraryLayout("media/Recordings", "media/SteamPoweredPlayer"), LibraryLocator.locate(share, "media"))
    }

    @Test fun recordings_without_a_data_folder_are_still_found() {
        val share = FakeShare().apply { put("clips/clip_1_20250101_120000/clip.pb") }
        assertEquals(LibraryLayout("", null), LibraryLocator.locate(share, ""))
    }

    @Test fun a_folder_without_recordings_is_not_a_library() {
        val share = FakeShare().apply { put("Docs/a.txt") }
        assertNull(LibraryLocator.locate(share, ""))
        val snapshot = repo(share).refresh()
        assertNull(snapshot.index)
        assertEquals(LibrarySource.None, snapshot.source)
        assertNotNull(snapshot.message)
    }

    @Test fun reads_the_desktop_index_and_keeps_it_for_offline() {
        val share = librarySetup()
        val snapshot = repo(share).refresh()
        assertEquals(LibrarySource.Index, snapshot.source)
        assertEquals(LibraryIndexStatus.Ok, snapshot.status)
        assertTrue(snapshot.index!!.recordings.isNotEmpty())

        share.offline = true
        val offline = repo(share).refresh()
        assertEquals(LibrarySource.Cached, offline.source)
        assertEquals(snapshot.index!!.recordings.size, offline.index!!.recordings.size)
        assertEquals("offline", offline.message)
    }

    @Test fun nothing_is_written_to_the_share() {
        val share = librarySetup()
        val before = share.content.keys.toSet()
        repo(share).refresh()
        assertEquals(before, share.content.keys.toSet())
    }

    @Test fun a_swap_of_the_index_is_retried_as_the_format_notes_say() {
        val share = librarySetup()
        share.failReads = 9
        assertEquals(LibrarySource.Index, repo(share).refresh().source)

        share.failReads = 0
        share.goneReads = 2
        assertEquals(LibrarySource.Index, repo(share).refresh().source)
    }

    @Test fun a_persistent_io_error_keeps_what_the_phone_knows() {
        val share = librarySetup()
        repo(share).refresh()
        share.failReads = 100
        val snapshot = repo(share).refresh()
        assertEquals(LibrarySource.Cached, snapshot.source)
        assertNotNull(snapshot.message)
    }

    @Test fun a_corrupt_index_falls_back_to_a_scan() {
        val share = librarySetup()
        share.put("SteamPoweredPlayer/library.json", "{ not json".toByteArray())
        share.put("Recordings/video/bg_730_20250101_120000/chunk-stream1-00001.m4s")
        val snapshot = repo(share).refresh()
        assertEquals(LibrarySource.Scan, snapshot.source)
        val r = snapshot.index!!.recordings.single()
        assertEquals("video/bg_730_20250101_120000", r.relativeFolder)
        assertEquals(listOf(SegmentRun(1, 1)), r.runs)
        assertTrue(r.temporary)
        assertEquals("App 730", snapshot.index!!.games.single().name)
    }

    @Test fun a_scan_finds_clips_by_their_media_folder_and_skips_empty_ones() {
        val share = FakeShare().apply {
            val media = "clips/clip_730_20250102_120000/video/bg_730_20250102_115000"
            put("$media/init-stream0.m4s")
            put("$media/chunk-stream0-00001.m4s")
            put("$media/chunk-stream1-00001.m4s")
            put("$media/chunk-stream0-00002.m4s")
            put("$media/chunk-stream1-00002.m4s")
            put("clips/clip_730_20250103_120000/video/bg_730_20250103_115000/init-stream0.m4s")
            put("clips/not-a-clip/video/x")
        }
        val index = repo(share).refresh().index!!
        val clip = index.recordings.single()
        assertEquals(RecordingKind.Clip, clip.kind)
        assertEquals(listOf(SegmentRun(1, 2)), clip.runs)
        assertEquals(6.0, clip.durationSeconds, 0.0)
        assertFalse(clip.temporary)
    }

    @Test fun refuses_a_relative_folder_leading_out_of_the_library() {
        val share = librarySetup()
        val text = Golden.text("library-v1.json")
        val folder = Regex(""""relativeFolder":\s*"([^"]+)"""").find(text)!!.groupValues[1]
        share.put("SteamPoweredPlayer/library.json", text.replace(folder, "../../secret").toByteArray())
        val snapshot = repo(share).refresh()
        assertTrue(snapshot.index!!.recordings.none { it.relativeFolder.contains("..") })
    }
}
