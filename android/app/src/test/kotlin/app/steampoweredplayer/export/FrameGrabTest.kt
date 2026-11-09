// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FrameGrabTest {
    private val frame = 1 / 60.0
    private val chunk1 = DoubleArray(180) { 3.0 + it * frame }
    private val chunk2 = DoubleArray(180) { 6.0 + it * frame }

    @Test
    fun theFrameOnScreenIsFoundByItsPlaceInItsChunk() {
        val p = FrameGrab.pick(mapOf(1 to chunk1, 2 to chunk2), 3.0 + 10 * frame, frame)!!
        assertEquals(1, p.chunk)
        assertEquals(10, p.index)
    }

    @Test
    fun aTimeJustOffAFrameStillFindsIt() {
        val p = FrameGrab.pick(mapOf(1 to chunk1, 2 to chunk2), 6.0 + 5 * frame + 0.002, frame)!!
        assertEquals(2, p.chunk)
        assertEquals(5, p.index)
    }

    @Test
    fun betweenFramesTheOneAlreadyShownWins() {
        val p = FrameGrab.pick(mapOf(1 to chunk1), 3.0 + 10.6 * frame, frame)!!
        assertEquals(10, p.index)
    }

    @Test
    fun aFrameRunningPastItsSlotIsFoundInTheNeighbour() {
        // A chunk's last frames can sit past the 3 s grid: the table, not the grid, decides.
        val late = DoubleArray(182) { 3.0 + it * frame }
        val p = FrameGrab.pick(mapOf(1 to late, 2 to DoubleArray(178) { 6.0 + (it + 2) * frame }), 6.0 + frame, frame)!!
        assertEquals(1, p.chunk)
        assertEquals(181, p.index)
    }

    @Test
    fun noTablesNoFrame() = assertNull(FrameGrab.pick(emptyMap(), 1.0, frame))
}
