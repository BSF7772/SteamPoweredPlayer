// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.nio.file.Files
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerProfileTest {
    private val good = ServerProfile(id = "a", name = "Home", host = "192.0.2.10", share = "Rec")

    @Test fun a_complete_profile_has_no_problems() = assertTrue(good.problems().isEmpty())

    @Test fun encryption_is_required_by_default() = assertTrue(good.requireEncryption)

    @Test fun bad_fields_are_named() {
        assertEquals(listOf(ProfileProblem.NoName), good.copy(name = " ").problems())
        listOf("", "smb://x", "pc/share", "\\\\pc", "my pc").forEach { assertEquals(it, listOf(ProfileProblem.BadHost), good.copy(host = it).problems()) }
        listOf("", "a/b", "a\\b").forEach { assertEquals(it, listOf(ProfileProblem.BadShare), good.copy(share = it).problems()) }
        assertEquals(listOf(ProfileProblem.BadPath), good.copy(path = "a/../b").problems())
        listOf(0, 65536).forEach { assertEquals(listOf(ProfileProblem.BadPort), good.copy(port = it).problems()) }
    }

    @Test fun urls_join_share_path_and_relative() {
        assertEquals("smb://192.0.2.10/Rec/", good.url())
        assertEquals("smb://192.0.2.10/Rec/Steam/rec/", good.copy(path = "\\Steam\\rec\\").url())
        assertEquals("smb://192.0.2.10/Rec/Steam/clips/a.m4s", good.copy(path = "Steam").url("clips/a.m4s"))
        assertEquals("smb://192.0.2.10/Rec/clips/", good.url("clips/"))
        assertEquals("smb://192.0.2.10:4445/Rec/", good.copy(port = 4445).url())
        assertEquals("smb://[fd7a::1]/Rec/", good.copy(host = "fd7a::1").url())
    }

    @Test fun the_first_reachable_profile_wins_in_list_order() {
        val vpn = good.copy(id = "b", name = "VPN")
        assertEquals(vpn, chooseEndpoint(listOf(good, vpn)) { it.id == "b" })
        assertEquals(good, chooseEndpoint(listOf(good, vpn)) { true })
        assertNull(chooseEndpoint(listOf(good, vpn)) { false })
    }

    @Test fun the_store_round_trips_and_a_bad_file_reads_as_empty() {
        val dir = Files.createTempDirectory("spp-profiles").toFile()
        try {
            val file = dir.resolve("servers.json")
            val store = ProfileStore(file)
            assertTrue(store.load().isEmpty())
            val profiles = listOf(good, good.copy(id = "b", requireEncryption = false, libraryId = "L"))
            store.save(profiles)
            assertEquals(profiles, store.load())
            store.save(profiles.take(1))
            assertEquals(profiles.take(1), store.load())
            assertFalse(file.readText().contains("password", ignoreCase = true))
            file.writeText("not json")
            assertTrue(store.load().isEmpty())
        } finally {
            dir.deleteRecursively()
        }
    }
}
