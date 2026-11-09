// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class LoopHandlesTest {
    @Test
    fun theFirstRangeSitsAroundThePlayhead() {
        val (a, b) = LoopHandles.initial(100.0, 0.0, 3600.0)
        assertTrue(a < 100.0 && b > 100.0)
        assertEquals(40.0, b - a, 1e-9)
    }

    @Test
    fun aShortClipGetsAShortRangeInsideIt() {
        val (a, b) = LoopHandles.initial(1.0, 0.0, 8.0)
        assertEquals(2.0, b - a, 1e-9)
        assertTrue(a >= 0.0 && b <= 8.0)
    }

    @Test
    fun theRangeStaysInsideAtTheEnd() {
        val (a, b) = LoopHandles.initial(3599.0, 0.0, 3600.0)
        assertEquals(3600.0, b, 1e-9)
        assertTrue(a < b)
    }

    @Test
    fun aRecordingShorterThanTheSmallestRangeIsCoveredWhole() {
        val (a, b) = LoopHandles.initial(0.1, 0.0, 0.3)
        assertEquals(0.0, a, 1e-9)
        assertEquals(0.3, b, 1e-9)
    }

    @Test
    fun handlesNeverCross() {
        assertEquals(9.5, LoopHandles.moveA(20.0, 10.0, 0.0), 1e-9)
        assertEquals(10.5, LoopHandles.moveB(1.0, 10.0, 60.0), 1e-9)
    }

    @Test
    fun handlesStayInsideTheRecording() {
        assertEquals(5.0, LoopHandles.moveA(1.0, 10.0, 5.0), 1e-9)
        assertEquals(60.0, LoopHandles.moveB(90.0, 10.0, 60.0), 1e-9)
    }

    @Test
    fun aPressTakesTheNearerHandleWithinReach() {
        assertEquals(TimelineGrab.A, LoopHandles.grab(105f, 100f, 200f, 24f))
        assertEquals(TimelineGrab.B, LoopHandles.grab(190f, 100f, 200f, 24f))
        assertEquals(TimelineGrab.B, LoopHandles.grab(112f, 100f, 120f, 24f))
    }

    @Test
    fun aPressAwayFromTheHandlesSeeks() {
        assertEquals(TimelineGrab.Seek, LoopHandles.grab(150f, 100f, 200f, 24f))
        assertEquals(TimelineGrab.Seek, LoopHandles.grab(150f, null, null, 24f))
    }

    @Test
    fun aPressMovesAboutFifteenFramesAndHoldingSpeedsUp() {
        assertEquals(15, LoopHandles.heldFrames(0))
        assertEquals(15, LoopHandles.heldFrames(999))
        assertTrue(LoopHandles.heldFrames(1_500) > LoopHandles.heldFrames(500))
        assertTrue(LoopHandles.heldFrames(3_000) > LoopHandles.heldFrames(1_500))
        assertTrue(LoopHandles.heldFrames(60_000) >= LoopHandles.heldFrames(3_000))
    }

    @Test
    fun anHourCanBeCrossedWhileHolding() {
        // Repeats every REPEAT_EVERY_MILLIS after REPEAT_AFTER_MILLIS; at 60 fps an hour should take well under a minute.
        val frame = LoopHandles.frameSeconds(60.0)
        var moved = 0.0
        var t = LoopHandles.REPEAT_AFTER_MILLIS
        while (moved < 3600.0 && t < 60_000) {
            moved += LoopHandles.heldFrames(t) * frame
            t += LoopHandles.REPEAT_EVERY_MILLIS
        }
        assertTrue("took $t ms", t < 30_000)
    }

    @Test
    fun theFrameLengthComesFromTheRateAndFallsBackTo60() {
        assertEquals(1.0 / 30.0, LoopHandles.frameSeconds(30.0), 1e-12)
        assertEquals(1.0 / 60.0, LoopHandles.frameSeconds(Double.NaN), 1e-12)
        assertEquals(1.0 / 60.0, LoopHandles.frameSeconds(0.0), 1e-12)
    }

    @Test
    fun aNudgeMovesOnlyTheSelectedHandleAndKeepsTheRangeValid() {
        assertEquals(10.25 to 20.0, LoopHandles.nudge(TimelineGrab.A, 0.25, 10.0, 20.0, 0.0, 60.0))
        assertEquals(10.0 to 23.0, LoopHandles.nudge(TimelineGrab.B, 3.0, 10.0, 20.0, 0.0, 60.0))
        assertEquals(0.0 to 20.0, LoopHandles.nudge(TimelineGrab.A, -30.0, 10.0, 20.0, 0.0, 60.0))
        assertEquals(10.0 to 60.0, LoopHandles.nudge(TimelineGrab.B, 99.0, 10.0, 20.0, 0.0, 60.0))
        val (a, b) = LoopHandles.nudge(TimelineGrab.A, 30.0, 10.0, 20.0, 0.0, 60.0)
        assertEquals(20.0 - LoopHandles.MIN_SECONDS, a, 1e-9)
        assertEquals(20.0, b, 1e-9)
        val (a2, b2) = LoopHandles.nudge(TimelineGrab.B, -30.0, 10.0, 20.0, 0.0, 60.0)
        assertEquals(10.0, a2, 1e-9)
        assertEquals(10.0 + LoopHandles.MIN_SECONDS, b2, 1e-9)
    }

    @Test
    fun bShowsTheLastKeptFrameAndAItsOwn() {
        assertEquals(10.0, LoopHandles.shownAt(TimelineGrab.A, 10.0, 20.0, 1 / 60.0), 1e-12)
        assertEquals(20.0 - 1 / 60.0, LoopHandles.shownAt(TimelineGrab.B, 10.0, 20.0, 1 / 60.0), 1e-12)
    }

    @Test
    fun aTapSelectsSwitchesOrLetsGo() {
        assertEquals(TimelineGrab.A, LoopHandles.tapped(null, TimelineGrab.A))
        assertEquals(null, LoopHandles.tapped(TimelineGrab.A, TimelineGrab.A))
        assertEquals(TimelineGrab.B, LoopHandles.tapped(TimelineGrab.A, TimelineGrab.B))
        assertEquals(null, LoopHandles.tapped(TimelineGrab.B, TimelineGrab.Seek))
    }
}
