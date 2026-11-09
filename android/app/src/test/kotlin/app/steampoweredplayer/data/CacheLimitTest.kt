// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class CacheLimitTest {
    @Test
    fun theDefaultIsFiveGigabytes() {
        assertEquals(5, CacheLimit.DEFAULT_GIGABYTES)
    }

    @Test
    fun aWholeNumberIsTheLimit() {
        assertEquals(12, CacheLimit.parse("12"))
        assertEquals(12, CacheLimit.parse(" 12 "))
    }

    @Test
    fun anythingElseChangesNothing() {
        for (text in listOf("", "0", "-5", "1.5", "abc", "999999999999")) assertNull(text, CacheLimit.parse(text))
    }
}
