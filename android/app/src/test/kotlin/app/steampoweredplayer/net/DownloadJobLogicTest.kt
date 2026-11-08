// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.net

import app.steampoweredplayer.data.ServerProfile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DownloadJobLogicTest {
    private fun profile(id: String, library: String? = null) = ServerProfile(id = id, name = id, host = "h", share = "s", libraryId = library)

    @Test fun a_request_survives_encoding() {
        val request = DownloadRequest("p", "bg_1", "video/bg_1", listOf(3, 4), playheadChunk = 3)
        assertEquals(request, DownloadRequest.decode(request.encode()))
        val whole = DownloadRequest("p", "bg_1", "video/bg_1")
        assertEquals(whole, DownloadRequest.decode(whole.encode()))
    }

    @Test fun sources_put_the_requested_profile_first_then_the_same_library_only() {
        val all = listOf(profile("vpn", "lib"), profile("other", "lib2"), profile("lan", "lib"))
        assertEquals(listOf("lan", "vpn"), DownloadRoutes.sources("lan", all)!!.map { it.id })
    }

    @Test fun a_profile_without_a_known_library_has_no_failover() {
        assertEquals(listOf("a"), DownloadRoutes.sources("a", listOf(profile("a"), profile("b")))!!.map { it.id })
    }

    @Test fun a_removed_profile_has_no_sources() {
        assertNull(DownloadRoutes.sources("gone", listOf(profile("a"))))
    }

    @Test fun the_library_key_is_the_library_once_known() {
        assertEquals("p", DownloadRoutes.libraryKey(profile("p")))
        assertEquals("lib", DownloadRoutes.libraryKey(profile("p", "lib")))
    }

    @Test fun only_chunks_finished_in_both_streams_count() {
        val names = listOf(
            "init-stream0.m4s", "init-stream1.m4s", "session.mpd",
            "chunk-stream0-00002.m4s", "chunk-stream1-00002.m4s",
            "chunk-stream0-00001.m4s", "chunk-stream1-00001.m4s",
            "chunk-stream0-00003.m4s",                              // audio not there yet
            "chunk-stream0-00004.m4s", "chunk-stream1-00004.m4s.tmp", // audio still being written
        )
        assertEquals(listOf(1, 2), DownloadRoutes.completeChunks(names))
    }

    @Test fun the_notice_applies_to_metered_or_data_saver_when_connected() {
        assertFalse(LinkState(connected = true).noticeApplies)
        assertTrue(LinkState(connected = true, metered = true).noticeApplies)
        assertTrue(LinkState(connected = true, dataSaver = true).noticeApplies)
        assertFalse(LinkState(connected = false, metered = true).noticeApplies)
    }

    @Test fun the_notice_shows_once_per_download() {
        val gate = MeteredNoticeGate { false }
        assertFalse(gate.shouldShow(LinkState(connected = true)))
        assertTrue(gate.shouldShow(LinkState(connected = true, metered = true)))
        assertFalse(gate.shouldShow(LinkState(connected = true, metered = true)))
    }

    @Test fun dont_show_again_is_final() {
        assertFalse(MeteredNoticeGate { true }.shouldShow(LinkState(connected = true, metered = true)))
    }
}
