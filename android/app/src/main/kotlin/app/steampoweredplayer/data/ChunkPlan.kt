// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

/** Which chunks a download fetches and in what order, as in the desktop's `Core/Net/ChunkPlan` (plan §4.3). A seek just asks for the order again. Pure. */
object ChunkPlan {
    /** The chunk at the playhead, then forward in time, then the ones behind it, nearest first. Cached chunks are skipped, duplicates removed. */
    fun order(selection: Iterable<Int>, playheadChunk: Int, isCached: (Int) -> Boolean): List<Int> {
        val wanted = selection.distinct().filterNot(isCached)
        return wanted.filter { it >= playheadChunk }.sorted() + wanted.filter { it < playheadChunk }.sortedDescending()
    }

    fun chunks(media: SessionMedia): List<Int> = media.runs.flatMap { it.first..it.last }

    fun range(media: SessionMedia, first: Int, last: Int): List<Int> = chunks(media).filter { it in first..last }

    /** The chunks within [marginSeconds] of each marker (media-axis seconds), limited to what [media] holds; sorted, no duplicates. */
    fun aroundMarkers(media: SessionMedia, markerSeconds: Iterable<Double>, marginSeconds: Double): List<Int> {
        require(marginSeconds >= 0)
        val all = chunks(media)
        return markerSeconds.flatMap { t ->
            val from = media.chunkAt(t - marginSeconds)
            val to = media.chunkAt(t + marginSeconds)
            all.filter { it in from..to }
        }.toSortedSet().toList()
    }

    /** 3 chunks at once on a healthy link, 2 once retries start to rise. */
    fun suggestedParallelism(recentRetries: Int): Int {
        require(recentRetries >= 0)
        return if (recentRetries >= 2) 2 else 3
    }
}
