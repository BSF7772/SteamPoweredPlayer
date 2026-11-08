// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.ByteArrayInputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.nio.file.Files
import kotlin.random.Random
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class ChunkPlanTest {
    private val media = SessionMedia("x", listOf(SegmentRun(1, 5), SegmentRun(9, 12)))

    @Test fun order_goes_forward_from_the_playhead_then_back_nearest_first() =
        assertEquals(listOf(4, 5, 9, 3, 2), ChunkPlan.order(listOf(2, 3, 4, 5, 9, 4), 4) { false })

    @Test fun cached_chunks_are_skipped() =
        assertEquals(listOf(5, 3), ChunkPlan.order(listOf(3, 4, 5), 4) { it == 4 })

    @Test fun range_keeps_what_exists() = assertEquals(listOf(4, 5, 9), ChunkPlan.range(media, 4, 9))

    @Test fun around_markers_merges_and_honours_the_axis_shift() {
        assertEquals(listOf(1, 2, 3, 4, 5, 9, 10, 11), ChunkPlan.aroundMarkers(media, listOf(6.0, 25.0, 7.0), 6.0))   // 25 ± 6 s reaches chunk 11
        val clip = SessionMedia("x", listOf(SegmentRun(1, 4)), axisShiftSeconds = 585.0)
        assertEquals(listOf(2, 3), ChunkPlan.aroundMarkers(clip, listOf(590.0), 2.0))
    }

    @Test fun parallelism_drops_when_retries_rise() {
        assertEquals(3, ChunkPlan.suggestedParallelism(1))
        assertEquals(2, ChunkPlan.suggestedParallelism(2))
    }
}

class ReconnectPolicyTest {
    private var now = 1_000L
    private fun policy(profiles: Int = 1) = ReconnectPolicy({ now }, Random(1), profiles)

    @Test fun a_drop_starts_reconnecting_with_an_immediate_first_attempt() {
        val p = policy()
        assertEquals(ReconnectState.Connected, p.state)
        p.onDisconnected()
        assertEquals(ReconnectState.Reconnecting, p.state)
        assertEquals(0, p.millisUntilNextAttempt)
    }

    @Test fun backoff_doubles_with_jitter_and_stops_at_the_cap() {
        val p = policy()
        p.onDisconnected()
        var ceiling = 1000L
        repeat(6) {
            p.onAttemptFailed()
            assertTrue("${p.currentDelayMillis} under $ceiling", p.currentDelayMillis in (ceiling / 2)..ceiling)
            ceiling = minOf(ceiling * 2, 10_000)
        }
    }

    @Test fun after_30_s_the_last_profile_cannot_be_reached() {
        val p = policy()
        p.onDisconnected()
        now += 29_999
        assertEquals(ReconnectState.Reconnecting, p.state)
        now += 1
        assertEquals(ReconnectState.TimedOut, p.state)
        assertEquals(TimedOutAction.CannotReach, p.action)
    }

    @Test fun a_timeout_moves_to_the_next_profile_with_a_fresh_clock() {
        val p = policy(2)
        p.onDisconnected()
        now += 30_000
        assertEquals(TimedOutAction.TryNextProfile, p.action)
        p.tryNextProfile()
        assertEquals(1, p.profileIndex)
        assertEquals(ReconnectState.Reconnecting, p.state)
        assertEquals(30_000, p.millisUntilTimeout)
    }

    @Test fun a_network_change_retries_now_from_the_first_profile() {
        val p = policy(2)
        p.onDisconnected()
        now += 30_000
        p.tryNextProfile()
        p.onAttemptFailed()
        p.onNetworkChanged()
        assertEquals(0, p.profileIndex)
        assertEquals(0, p.millisUntilNextAttempt)
        assertEquals(30_000, p.millisUntilTimeout)
    }

    @Test fun a_second_drop_keeps_the_clock_and_a_change_while_connected_is_ignored() {
        val p = policy()
        p.onNetworkChanged()
        assertEquals(ReconnectState.Connected, p.state)
        p.onDisconnected()
        now += 10_000
        p.onDisconnected()
        assertEquals(20_000, p.millisUntilTimeout)
    }

    @Test fun connecting_ends_the_outage_even_after_a_timeout() {
        val p = policy()
        p.onDisconnected()
        now += 30_000
        p.onConnected()
        assertEquals(ReconnectState.Connected, p.state)
    }
}

class LocalChunkCacheTest {
    private val dir: File = Files.createTempDirectory("spp-cache").toFile()

    @After fun clean() {
        dir.deleteRecursively()
    }

    private fun key(chunk: Int, rec: String = "bg_1") = ChunkKey("lib", rec, 0, chunk)
    private fun bytes(n: Int) = ByteArrayInputStream(ByteArray(n))

    @Test fun files_keep_steams_names() {
        val cache = LocalChunkCache.open(dir, 1000)
        assertEquals("chunk-stream0-00007.m4s", cache.put(key(7), bytes(10), 10).name)
        assertEquals("init-stream1.m4s", cache.put(ChunkKey("lib", "bg_1", 1, 0), bytes(10), 10).name)
        assertEquals(20, cache.totalBytes)
    }

    @Test fun names_that_could_leave_the_cache_are_refused() {
        listOf("..", ".", "a/b", "a\\b", "").forEach { bad ->
            assertThrows(bad, IllegalArgumentException::class.java) { ChunkKey(bad, "r", 0, 1) }
            assertThrows(bad, IllegalArgumentException::class.java) { ChunkKey("l", bad, 0, 1) }
        }
    }

    @Test fun a_wrong_size_leaves_nothing() {
        val cache = LocalChunkCache.open(dir, 1000)
        assertThrows(IOException::class.java) { cache.put(key(1), bytes(5), 10) }
        assertThrows(IOException::class.java) { cache.put(key(2), bytes(11), 10) }
        assertFalse(key(1) in cache)
        assertEquals(0, cache.partialLength(key(1)))
        assertEquals(0, cache.totalBytes)
    }

    @Test fun a_dropped_read_keeps_the_part_and_a_retry_resumes_from_it() {
        val cache = LocalChunkCache.open(dir, 1000)
        val dropping = object : InputStream() {
            private var left = 4
            override fun read(): Int = if (left-- > 0) 1 else throw IOException("dropped")
        }
        assertThrows(IOException::class.java) { cache.put(key(1), dropping, 10) }
        assertFalse(key(1) in cache)
        assertEquals(4, cache.partialLength(key(1)))
        cache.put(key(1), bytes(6), 10, resume = true)
        assertEquals(10, cache.pathOf(key(1))!!.length())
        assertEquals(0, cache.partialLength(key(1)))
    }

    @Test fun oldest_goes_first_and_a_use_makes_it_young() {
        val cache = LocalChunkCache.open(dir, 30)
        (1..3).forEach { cache.put(key(it), bytes(10), 10) }
        assertNotNull(cache.pathOf(key(1)))
        cache.put(key(4), bytes(10), 10)
        assertTrue(key(1) in cache)
        assertFalse(key(2) in cache)
        assertEquals(30, cache.totalBytes)
    }

    @Test fun pins_leases_and_the_new_entry_survive_so_the_cap_can_be_exceeded() {
        val cache = LocalChunkCache.open(dir, 10)
        cache.pin("lib", "kept")
        cache.put(key(1, "kept"), bytes(10), 10)
        cache.put(key(2), bytes(10), 10)
        val lease = cache.markInUse(key(2))
        cache.put(key(3), bytes(10), 10)
        assertTrue(key(1, "kept") in cache)
        assertTrue(key(2) in cache)
        assertTrue(key(3) in cache)
        lease.close()
        lease.close()
        cache.put(key(4), bytes(10), 10)
        assertFalse(key(2) in cache)
        assertFalse(key(3) in cache)
        assertTrue(key(1, "kept") in cache)
    }

    @Test fun reopening_keeps_files_order_and_pins_and_ignores_foreign_files() {
        LocalChunkCache.open(dir, 30).apply {
            pin("lib", "bg_1")
            (1..2).forEach { put(key(it), bytes(10), 10) }
            pathOf(key(1))
            flush()
        }
        File(dir, "lib/bg_1/notes.txt").writeText("not ours")
        val again = LocalChunkCache.open(dir, 30)
        assertEquals(20, again.totalBytes)
        assertTrue(again.isPinned("lib", "bg_1"))
        assertTrue(File(dir, "lib/bg_1/notes.txt").exists())
    }

    @Test fun a_corrupt_index_costs_only_order_and_pins() {
        LocalChunkCache.open(dir, 30).put(key(1), bytes(10), 10)
        File(dir, "cache-index.json").writeText("garbage")
        val again = LocalChunkCache.open(dir, 30)
        assertTrue(key(1) in again)
        assertFalse(again.isPinned("lib", "bg_1"))
    }

    @Test fun a_vanished_file_reads_as_missing() {
        val cache = LocalChunkCache.open(dir, 30)
        cache.put(key(1), bytes(10), 10).delete()
        assertNull(cache.pathOf(key(1)))
        assertEquals(0, cache.totalBytes)
    }
}
