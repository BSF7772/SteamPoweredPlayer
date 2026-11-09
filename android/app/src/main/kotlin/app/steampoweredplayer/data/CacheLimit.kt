// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

/** The playback cache's limit as the user types it. Pure. */
object CacheLimit {
    const val DEFAULT_GIGABYTES = 5
    private const val MAX_GIGABYTES = 100_000

    /** Whole gigabytes, at least one. Null for anything else, so a half-typed field never changes the limit. */
    fun parse(text: String): Int? = text.trim().toIntOrNull()?.takeIf { it in 1..MAX_GIGABYTES }
}
