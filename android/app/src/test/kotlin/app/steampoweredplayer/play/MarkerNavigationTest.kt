// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class MarkerNavigationTest {
    @Test
    fun nowGoesBeforeTheFirstLaterEvent() {
        val starts = listOf(10.0, 20.0, 30.0)
        assertEquals(0, MarkerNavigation.nowIndex(starts, 5.0))
        assertEquals(1, MarkerNavigation.nowIndex(starts, 10.0))
        assertEquals(2, MarkerNavigation.nowIndex(starts, 25.0))
        assertEquals(3, MarkerNavigation.nowIndex(starts, 99.0))
        assertEquals(0, MarkerNavigation.nowIndex(emptyList(), 1.0))
    }

    @Test
    fun aShortListIsShownWhole() {
        assertEquals(0 until 5, MarkerNavigation.windowAround(5, 3, 200))
    }

    @Test
    fun aLongListIsCutAroundNow() {
        val w = MarkerNavigation.windowAround(1000, 600, 200)
        assertEquals(200, w.count())
        assertTrue(600 in w)
        assertEquals(0 until 200, MarkerNavigation.windowAround(1000, 3, 200))
        assertEquals(800 until 1000, MarkerNavigation.windowAround(1000, 1000, 200))
    }
}
