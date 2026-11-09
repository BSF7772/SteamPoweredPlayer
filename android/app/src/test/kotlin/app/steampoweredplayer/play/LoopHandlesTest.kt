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
}
