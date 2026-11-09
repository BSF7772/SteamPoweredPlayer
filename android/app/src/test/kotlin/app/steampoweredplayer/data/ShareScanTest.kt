// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.IOException
import java.io.InputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ShareScanTest {
    /** Folders by path, each with its names; anything else is gone. */
    private class Tree(private val folders: Map<String, List<String>>, private val denied: Set<String> = emptySet()) : RemoteFiles {
        val listed = mutableListOf<String>()

        override fun list(relativeFolder: String): List<String> {
            listed += relativeFolder
            if (relativeFolder in denied) throw IOException("access denied")
            return folders[relativeFolder] ?: throw RemoteFileGone(relativeFolder)
        }

        override fun size(relativePath: String): Long = throw RemoteFileGone(relativePath)

        override fun open(relativePath: String, offset: Long): InputStream = throw RemoteFileGone(relativePath)
    }

    @Test
    fun adminSharesAreHiddenAndEachShareListedOnce() {
        assertEquals(listOf("Media", "Users"), ShareScan.visible(listOf("Users/", "IPC$", "C$", "media", "Media/").reversed()))
    }

    @Test
    fun recordingsAtTheRootAreFound() {
        assertEquals("", ShareScan.findRecordings(Tree(mapOf("" to listOf("clips", "video"), "clips" to listOf("clip_440_20200101_120000")))))
    }

    @Test
    fun recordingsAreFoundInADeeperFolder() {
        val tree = Tree(
            mapOf(
                "" to listOf("Games", "Music"),
                "Music" to emptyList(),
                "Games" to listOf("Steam"),
                "Games/Steam" to listOf("recordings"),
                "Games/Steam/recordings" to listOf("video", "clips"),
                "Games/Steam/recordings/video" to listOf("bg_440_20200101_120000"),
                "Games/Steam/recordings/clips" to emptyList(),
            ),
        )
        assertEquals("Games/Steam/recordings", ShareScan.findRecordings(tree))
    }

    @Test
    fun steamsOwnFileIsEnough() {
        assertEquals("rec", ShareScan.findRecordings(Tree(mapOf("" to listOf("rec"), "rec" to listOf("gamerecording.pb")))))
    }

    @Test
    fun ordinaryFoldersCalledVideoOrClipsDontCount() {
        val tree = Tree(mapOf("" to listOf("video", "clips"), "video" to listOf("holiday"), "clips" to listOf("funny"), "video/holiday" to emptyList(), "clips/funny" to emptyList()))
        assertNull(ShareScan.findRecordings(tree))
    }

    @Test
    fun theShallowestLibraryWins() {
        val tree = Tree(
            mapOf(
                "" to listOf("a", "b"),
                "a" to listOf("deep"),
                "a/deep" to listOf("gamerecording.pb"),
                "b" to listOf("gamerecording.pb"),
            ),
        )
        assertEquals("b", ShareScan.findRecordings(tree))
    }

    @Test
    fun aFolderThatCantBeReadIsSkipped() {
        val tree = Tree(mapOf("" to listOf("private", "ok"), "ok" to listOf("gamerecording.pb")), denied = setOf("private"))
        assertEquals("ok", ShareScan.findRecordings(tree))
    }

    @Test
    fun systemFoldersAndFilesAreNotEntered() {
        val tree = Tree(mapOf("" to listOf("AppData", "Windows", ".git", "notes.txt", "Admin$"), "AppData" to listOf("gamerecording.pb")))
        assertNull(ShareScan.findRecordings(tree))
        assertEquals(listOf(""), tree.listed)
    }

    @Test
    fun theSearchIsBounded() {
        val folders = HashMap<String, List<String>>()
        folders[""] = (0 until 1000).map { "f$it" }
        for (i in 0 until 1000) folders["f$i"] = emptyList()
        val tree = Tree(folders)
        assertNull(ShareScan.findRecordings(tree))
        assertTrue(tree.listed.size <= 200)
    }

    @Test
    fun theSearchStopsWhenAsked() {
        val tree = Tree(mapOf("" to listOf("a"), "a" to listOf("gamerecording.pb")))
        assertNull(ShareScan.findRecordings(tree) { false })
        assertTrue(tree.listed.isEmpty())
    }

    @Test
    fun theShareWithRecordingsIsPreferred() {
        val found = listOf(FoundShare("Public", null), FoundShare("Media", "steam"))
        assertEquals("Media", ShareScan.preferred(found)?.name)
        assertNull(ShareScan.preferred(listOf(FoundShare("Public", null))))
    }
}
