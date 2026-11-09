// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

/** A screenshot's file name: the game, then when it was taken, as on the desktop ("Game 2026-11-09 21-30-05.png"). Pure. */
object ScreenshotNames {
    private const val MAX_STEM = 120

    fun of(title: String, stamp: String): String {
        val game = title.map { if (it in "\\/:*?\"<>|" || it.isISOControl()) ' ' else it }.joinToString("").replace(Regex(" +"), " ").trim().take(MAX_STEM - stamp.length - 1).trim()
        return (if (game.isEmpty()) "Screenshot $stamp" else "$game $stamp") + ".png"
    }
}
