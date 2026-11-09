// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import app.steampoweredplayer.data.Marker
import app.steampoweredplayer.data.MarkerAlignment
import app.steampoweredplayer.data.SegmentRun
import app.steampoweredplayer.data.SessionMedia
import app.steampoweredplayer.data.TimelineEntry
import app.steampoweredplayer.data.TimelineEntryType
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PlayableRunsTest {
    private val full = SessionMedia("/m", listOf(SegmentRun(1, 10), SegmentRun(20, 25)))

    @Test fun the_player_gets_only_the_stretch_it_can_read() {
        val here = setOf(1, 2, 3, 5, 6, 8, 9, 10, 20, 21)
        assertEquals(listOf(SegmentRun(5, 6)), PlayableRuns.around(full, here, 13.0)!!.runs)
        assertEquals(listOf(SegmentRun(1, 3)), PlayableRuns.around(full, here, 0.0)!!.runs)
        assertNull(PlayableRuns.around(full, here, 10.0))                             // chunk 4 is still coming
    }

    @Test fun a_stretch_stops_at_a_gap_of_the_recording() {
        val here = (1..10).toSet() + (20..25)
        assertEquals(listOf(SegmentRun(1, 10)), PlayableRuns.around(full, here, 29.0)!!.runs)
        assertNull(PlayableRuns.around(full, here, 40.0))                             // the gap has no chunk
    }

    @Test fun a_clips_limits_stay_on_the_stretch() {
        val clip = SessionMedia("/m", listOf(SegmentRun(1, 4)), startLimitSeconds = 1.0, endLimitSeconds = 10.5)
        val part = PlayableRuns.around(clip, setOf(4), 10.0)!!
        assertEquals(10.5, part.endSeconds, 0.0)
        val first = PlayableRuns.around(clip, setOf(1, 2), 2.0)!!
        assertEquals(1.0, first.startSeconds, 0.0)
        assertEquals(6.0, first.endSeconds, 0.0)
    }

    @Test fun next_chunks_cross_gaps_and_end() {
        assertEquals(20, PlayableRuns.chunkAfter(full, 10))
        assertEquals(7, PlayableRuns.chunkAfter(full, 6))
        assertNull(PlayableRuns.chunkAfter(full, 25))
        assertEquals(20, PlayableRuns.nextChunkAt(full, 40.0))
        assertEquals(1, PlayableRuns.nextChunkAt(full, 0.5))
        assertNull(PlayableRuns.nextChunkAt(full, 900.0))
    }

    @Test fun a_small_recording_comes_whole_and_a_long_one_follows_the_playhead() {
        val long = SessionMedia("/m", listOf(SegmentRun(1, 500)))
        assertEquals(500, FetchPolicy.selection(long, 900L * 1024 * 1024, 100).size)
        val window = FetchPolicy.selection(long, 8L * 1024 * 1024 * 1024, 100)
        assertEquals(100, window.first())
        assertEquals(FetchPolicy.AHEAD_CHUNKS, window.size)
        assertEquals(500, FetchPolicy.selection(long, 8L * 1024 * 1024 * 1024, 490).last())
    }

    @Test fun top_ups_come_when_the_chunks_ahead_run_low() {
        val long = SessionMedia("/m", listOf(SegmentRun(1, 100)))
        assertTrue(FetchPolicy.needsTopUp(long, (1..10).toSet(), 5))
        assertFalse(FetchPolicy.needsTopUp(long, (1..50).toSet(), 5))
        assertFalse(FetchPolicy.needsTopUp(long, (1..100).toSet(), 90))
    }

    private fun marker(at: Double, type: TimelineEntryType = TimelineEntryType.Event, icon: String? = null, duration: Long = 0) = Marker(
        TimelineEntry(null, 0, type, "event", null, null, icon, null, duration, false, null, emptyList(), null, null), at, null, MarkerAlignment.Exact,
    )

    @Test fun events_are_classified_as_on_the_desktop() {
        assertEquals(MarkerKind.Kill, MarkerKinds.classify(marker(0.0, icon = "cs2_gun_kill").entry))
        assertEquals(MarkerKind.MultiKill, MarkerKinds.classify(marker(0.0, icon = "cs2_gun_kill", duration = 500).entry))
        assertEquals(MarkerKind.Death, MarkerKinds.classify(marker(0.0, icon = "cs2_death").entry))
        assertEquals(MarkerKind.Objective, MarkerKinds.classify(marker(0.0, icon = "cs2_bomb_plant").entry))
        assertEquals(MarkerKind.Phase, MarkerKinds.classify(marker(0.0, TimelineEntryType.GameMode).entry))
        assertFalse(MarkerKinds.showOnLane(MarkerKind.Phase))
    }

    @Test fun next_and_previous_walk_the_list() {
        val list = listOf(marker(10.0), marker(20.0), marker(30.0))
        assertEquals(10.0, MarkerNavigation.next(list, 0.0)!!.startSeconds, 0.0)
        assertEquals(20.0, MarkerNavigation.next(list, 7.5)!!.startSeconds, 0.0)      // just landed on the first: the following one
        assertNull(MarkerNavigation.next(list, 28.0))
        assertEquals(10.0, MarkerNavigation.previous(list, 17.5)!!.startSeconds, 0.0)
        assertNull(MarkerNavigation.previous(list, 7.5))
    }

    @Test fun times_read_like_a_player() {
        assertEquals("0:05", TimeText.of(5.9))
        assertEquals("1:02:03", TimeText.of(3723.0))
    }
}
