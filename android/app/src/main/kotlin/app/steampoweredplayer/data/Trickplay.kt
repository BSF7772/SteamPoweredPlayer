// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import kotlin.math.floor
import kotlin.math.max
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

@Serializable
data class TrickplaySheet(val file: String, val chunks: List<Int>)

/** `<data>/trick/<recording key>/manifest.json`, written by the desktop: JPEG sprite sheets of the first frame of each chunk. Keyed by chunk number. */
@Serializable
data class TrickplayManifest(
    val version: Int,
    val tileWidth: Int,
    val tileHeight: Int,
    val columns: Int,
    val rows: Int,
    val intervalSeconds: Double,
    val sheets: List<TrickplaySheet> = emptyList(),
) {
    val capacity: Int get() = columns * rows

    companion object {
        const val CURRENT_VERSION = 1
        const val FILE_NAME = "manifest.json"
        private val json = Json { ignoreUnknownKeys = true }

        /** Null for malformed JSON, another version or implausible values. */
        fun parse(text: String): TrickplayManifest? = try {
            json.decodeFromString<TrickplayManifest>(text).takeIf {
                it.version == CURRENT_VERSION && it.columns > 0 && it.rows > 0 && it.tileWidth > 0 && it.tileHeight >= 0 && it.intervalSeconds > 0
            }
        } catch (_: SerializationException) {
            null
        } catch (_: IllegalArgumentException) {
            null
        }
    }
}

/** A thumbnail: the rectangle of [sheet] that shows [chunk]. */
data class TrickplayTile(val chunk: Int, val sheet: String, val x: Int, val y: Int, val width: Int, val height: Int)

/** Finds the thumbnail for a media time, as in the desktop's `TrickplayIndex`. [axisShiftSeconds] is the recording's. Pure. */
class TrickplayIndex(private val manifest: TrickplayManifest, private val axisShiftSeconds: Double = 0.0) {
    private val byChunk = HashMap<Int, Pair<Int, Int>>()
    private val sorted: IntArray

    init {
        manifest.sheets.forEachIndexed { s, sheet ->
            sheet.chunks.forEachIndexed { t, chunk -> if (t < manifest.capacity && chunk > 0) byChunk[chunk] = s to t }
        }
        sorted = byChunk.keys.sorted().toIntArray()
    }

    val count: Int get() = sorted.size

    fun chunkAt(seconds: Double): Int =
        if (seconds.isNaN()) 1 else (floor(max(0.0, seconds - axisShiftSeconds) / manifest.intervalSeconds) + 1).coerceAtMost(Int.MAX_VALUE.toDouble()).toInt()

    fun findChunk(chunk: Int): TrickplayTile? = byChunk[chunk]?.let { (s, t) ->
        TrickplayTile(chunk, manifest.sheets[s].file, t % manifest.columns * manifest.tileWidth, t / manifest.columns * manifest.tileHeight, manifest.tileWidth, manifest.tileHeight)
    }

    /** The thumbnail nearest to [seconds]: a gap resolves to the closest chunk that has one, the earlier one on a tie. Null only when there is none. */
    fun findNearest(seconds: Double): TrickplayTile? {
        if (sorted.isEmpty()) return null
        val c = chunkAt(seconds)
        val i = sorted.binarySearch(c)
        if (i >= 0) return findChunk(c)
        val after = -i - 1
        if (after == 0) return findChunk(sorted.first())
        if (after == sorted.size) return findChunk(sorted.last())
        val b = sorted[after - 1]
        val a = sorted[after]
        return findChunk(if (c - b <= a - c) b else a)
    }
}
