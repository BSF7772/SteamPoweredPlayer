// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import app.steampoweredplayer.data.SessionMedia

enum class ReloadReason { None, OutsideWindow, WindowStale, NearWindowEnd, Periodic }

/** Which ~60 chunks go into the EDL and when a loaded one must be rebuilt, as in the desktop's `EdlWindow`. Pure: no I/O. */
object EdlWindow {
    const val DEFAULT_COUNT = 60
    const val LOOK_BEHIND_DIVISOR = 4
    const val RELOAD_MARGIN_CHUNKS = 10
    const val PERIODIC_RELOAD_MEDIA_SECONDS = 600.0

    fun window(media: SessionMedia, positionSeconds: Double, count: Int = DEFAULT_COUNT): List<Int> {
        require(count > 0)
        val total = media.chunkCount
        if (total == 0) return emptyList()
        val anchor = indexOfPosition(media, positionSeconds)
        val start = (anchor - count / LOOK_BEHIND_DIVISOR).coerceAtMost(total - count).coerceAtLeast(0)
        return media.runs.asSequence().flatMap { it.first..it.last }.drop(start).take(count).toList()
    }

    fun shouldReload(window: List<Int>, media: SessionMedia, playheadSeconds: Double, mediaSecondsSinceLoad: Double): ReloadReason {
        if (media.chunkCount == 0) return ReloadReason.None
        if (window.isEmpty()) return ReloadReason.OutsideWindow
        val first = window.first()
        val last = window.last()
        val atStart = first == media.firstChunk
        val atEnd = last == media.lastChunk
        val chunk = media.chunkAt(playheadSeconds)
        return when {
            (!atStart && chunk < first) || (!atEnd && chunk > last) -> ReloadReason.OutsideWindow
            !media.contains(first) || !media.contains(last) -> ReloadReason.WindowStale
            !atEnd && last - chunk <= RELOAD_MARGIN_CHUNKS -> ReloadReason.NearWindowEnd
            mediaSecondsSinceLoad >= PERIODIC_RELOAD_MEDIA_SECONDS -> ReloadReason.Periodic
            else -> ReloadReason.None
        }
    }

    private fun indexOfPosition(media: SessionMedia, positionSeconds: Double): Int {
        val chunk = media.chunkAt(positionSeconds)
        var index = 0
        for (run in media.runs) {
            if (chunk < run.first) return index
            if (chunk <= run.last) return index + (chunk - run.first).toInt()
            index += run.count
        }
        return index - 1
    }
}
