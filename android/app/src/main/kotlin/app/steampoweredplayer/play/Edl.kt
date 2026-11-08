// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.SessionMedia
import java.math.BigDecimal

/**
 * The lead mpv EDL, byte for byte the desktop's (`Core/Playback/Edl.cs` holds the derivation; the golden files keep them equal).
 * Pure: no I/O, no mpv.
 */
object Edl {
    /** [chunks] are the sorted, distinct chunk numbers of one window; [url] maps a path to what mpv opens, such as a cache path. */
    fun build(media: SessionMedia, chunks: List<Int>, url: (String) -> String = { it }): String {
        require(chunks.isNotEmpty()) { "An EDL needs at least one chunk." }
        require(chunks.zipWithNext().all { (a, b) -> b > a }) { "Chunk numbers must be strictly ascending." }
        val dir = media.folder.replace('\\', '/').trimEnd('/')
        return buildString {
            append("# mpv EDL v0\n!no_chapters\n")
            track(media, dir, chunks, 0, url)
            append("!new_stream\n")
            track(media, dir, chunks, 1, url)
        }
    }

    private fun StringBuilder.track(media: SessionMedia, dir: String, chunks: List<Int>, stream: Int, url: (String) -> String) {
        append("!mp4_dash,init=").append(escape(url("$dir/${FolderNames.initFile(stream)}"))).append('\n')
        val seg = media.segmentSeconds
        chunks.forEachIndexed { i, n ->
            val next = chunks.getOrNull(i + 1) ?: (n + 1)
            val length = if (i == 0) (next - 1) * seg + media.axisShiftSeconds else (next - n) * seg
            append(escape(url("$dir/${FolderNames.chunkFile(stream, n)}"))).append(",length=").append(number(length)).append('\n')
        }
    }

    /** mpv's length-prefixed value syntax: `%<utf8 byte count>%value`. */
    fun escape(value: String): String = "%${value.toByteArray(Charsets.UTF_8).size}%$value"

    /** Shortest form without an exponent and without `.0`, as .NET prints a double: 5520, 1.5. */
    private fun number(v: Double): String = BigDecimal(v.toString()).stripTrailingZeros().toPlainString()
}
