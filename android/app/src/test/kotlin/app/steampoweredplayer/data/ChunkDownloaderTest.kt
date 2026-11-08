// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.ByteArrayInputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.nio.file.Files
import kotlin.random.Random
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ChunkDownloaderTest {
    private val dir: File = Files.createTempDirectory("spp-dl").toFile()
    private val fileSize = 100L

    @After fun clean() {
        dir.deleteRecursively()
    }

    /** A share in memory. [down] refuses everything; [dropAfter] ends a read with a failure after so many bytes, once per file. */
    private inner class FakeShare(val gone: Set<String> = emptySet()) : RemoteFiles {
        var down = false
        var dropAfter: Long? = null
        val opened = ArrayList<Pair<String, Long>>()
        private val dropped = HashSet<String>()

        override fun list(relativeFolder: String): List<String> = emptyList()

        override fun size(relativePath: String): Long {
            if (down) throw IOException("down")
            if (relativePath.substringAfterLast('/') in gone) throw RemoteFileGone(relativePath)
            return fileSize
        }

        override fun open(relativePath: String, offset: Long): InputStream {
            if (down) throw IOException("down")
            synchronized(opened) { opened.add(relativePath.substringAfterLast('/') to offset) }
            val data = ByteArrayInputStream(ByteArray((fileSize - offset).toInt()))
            val cut = dropAfter?.takeIf { synchronized(dropped) { dropped.add(relativePath) } } ?: return data
            return object : InputStream() {
                private var left = cut
                override fun read(): Int = if (left-- > 0) data.read() else throw IOException("dropped")
            }
        }
    }

    private fun TestScope.downloader(vararg shares: RemoteFiles, workers: Int = 1): Pair<ChunkDownloader, LocalChunkCache> {
        val cache = LocalChunkCache.open(dir, Long.MAX_VALUE)
        val policy = ReconnectPolicy({ testScheduler.currentTime }, Random(1), shares.size)
        val io = UnconfinedTestDispatcher(testScheduler)
        return ChunkDownloader(cache, "lib", "bg_1", "video/bg_1", shares.toList(), policy, io, workers) to cache
    }

    private fun key(stream: Int, chunk: Int) = ChunkKey("lib", "bg_1", stream, chunk)

    @Test fun fetches_init_first_then_from_the_playhead_then_behind_it() = runTest {
        val share = FakeShare()
        val (d, cache) = downloader(share)
        d.select(listOf(1, 2, 3), playheadChunk = 2)
        d.run()
        assertEquals(
            listOf(
                "init-stream0.m4s", "init-stream1.m4s", "chunk-stream0-00002.m4s", "chunk-stream1-00002.m4s",
                "chunk-stream0-00003.m4s", "chunk-stream1-00003.m4s", "chunk-stream0-00001.m4s", "chunk-stream1-00001.m4s",
            ),
            share.opened.map { it.first },
        )
        assertEquals(DownloadStatus.Done, d.progress.value.status)
        assertEquals(8, d.progress.value.filesDone)
        assertEquals(800, d.progress.value.bytesFromSource)
        assertEquals(800, cache.totalBytes)
    }

    @Test fun what_is_cached_already_is_not_fetched_again() = runTest {
        val share = FakeShare()
        val (d, cache) = downloader(share)
        cache.put(key(0, 1), ByteArrayInputStream(ByteArray(100)), 100)
        d.select(listOf(1), 1)
        d.run()
        assertFalse(share.opened.any { it.first == "chunk-stream0-00001.m4s" })
        assertEquals(3, d.progress.value.filesTotal)
    }

    @Test fun a_seek_moves_the_next_fetch() = runTest {
        val share = FakeShare()
        val (d, _) = downloader(share)
        d.select(listOf(1, 2, 3), 1)
        d.seek(3)
        d.run()
        assertEquals("chunk-stream0-00003.m4s", share.opened[2].first)
    }

    @Test fun a_drop_resumes_from_the_part_and_loses_only_what_was_in_flight() = runTest {
        val share = FakeShare().apply { dropAfter = 40 }
        val (d, cache) = downloader(share)
        d.select(listOf(1), 1)
        val job = launch { d.run() }
        runCurrent()
        testScheduler.advanceUntilIdle()
        job.join()
        assertEquals(4, d.progress.value.filesDone)
        assertEquals(400, d.progress.value.bytesFromSource)   // not one byte fetched twice
        assertTrue(share.opened.contains("init-stream0.m4s" to 40L))
        assertEquals(400, cache.totalBytes)
    }

    @Test fun a_file_steam_trimmed_is_skipped_without_retrying() = runTest {
        val share = FakeShare(gone = setOf("chunk-stream1-00001.m4s"))
        val (d, _) = downloader(share)
        d.select(listOf(1, 2), 1)
        d.run()
        assertEquals(1, d.progress.value.filesGone)
        assertEquals(5, d.progress.value.filesDone)
    }

    @Test fun after_30_s_down_the_next_source_takes_over() = runTest {
        val lan = FakeShare().apply { down = true }
        val vpn = FakeShare()
        val (d, cache) = downloader(lan, vpn)
        d.select(listOf(1), 1)
        val job = launch { d.run() }
        advanceTimeBy(29_000)
        runCurrent()
        assertEquals(0, d.progress.value.filesDone)
        assertEquals(DownloadStatus.Reconnecting, d.progress.value.status)
        testScheduler.advanceUntilIdle()
        job.join()
        assertEquals(1, d.progress.value.profileIndex)
        assertEquals(4, d.progress.value.filesDone)
        assertEquals(400, cache.totalBytes)
    }

    @Test fun with_no_source_left_it_waits_for_a_network_change_and_then_finishes() = runTest {
        val share = FakeShare().apply { down = true }
        val (d, _) = downloader(share, workers = 2)
        d.select(listOf(1), 1)
        val job = launch { d.run() }
        advanceTimeBy(31_000)
        runCurrent()
        testScheduler.advanceTimeBy(20_000)
        runCurrent()
        assertEquals(DownloadStatus.CannotReach, d.progress.value.status)

        // It stays waiting, with no polling: time alone changes nothing.
        testScheduler.advanceTimeBy(600_000)
        runCurrent()
        assertEquals(DownloadStatus.CannotReach, d.progress.value.status)

        share.down = false
        d.onNetworkChanged()
        testScheduler.advanceUntilIdle()
        job.join()
        assertEquals(DownloadStatus.Done, d.progress.first().status)
        assertEquals(4, d.progress.value.filesDone)
    }
}
