// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ScreenshotNamesTest {
    @Test
    fun theGameThenTheTime() = assertEquals("Space Game 2026-11-09 21-30-05.png", ScreenshotNames.of("Space Game", "2026-11-09 21-30-05"))

    @Test
    fun charactersAFileNameCantHoldBecomeSpaces() = assertEquals("A B C 2026-11-09 21-30-05.png", ScreenshotNames.of("A:B/C?", "2026-11-09 21-30-05"))

    @Test
    fun noTitleStillGivesAName() = assertEquals("Screenshot 2026-11-09 21-30-05.png", ScreenshotNames.of("  ", "2026-11-09 21-30-05"))

    @Test
    fun aLongTitleIsShortened() = assertTrue(ScreenshotNames.of("x".repeat(500), "2026-11-09 21-30-05").length <= 124)
}
