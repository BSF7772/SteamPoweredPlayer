// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.time.format.DateTimeParseException
import java.time.format.ResolverStyle
import java.util.Locale

enum class RecordingKind { CacheSession, Clip, DownloadedClip }

data class RecordingName(val kind: RecordingKind, val gameId: ULong?, val startUtc: Instant)
data class TimelineName(val gameId: ULong, val startUtc: Instant)
data class MediaFolderName(val foreground: Boolean, val gameId: ULong, val startUtc: Instant, val index: Int?)
data class ChunkName(val stream: Int, val number: Int, val temporary: Boolean)

/** Steam's folder and file names, as in the desktop's Core (docs/steam-recording-format.md). Pure: no I/O. All times are UTC. */
object FolderNames {
    private val recording = Regex("""^(bg|clip)_(\d+)_(\d{8})_(\d{6})$""")
    private val downloaded = Regex("""^clip_downloaded_(\d{8})_(\d{6})$""")
    private val timeline = Regex("""^timeline_(\d+)(\d{8})_(\d{6})\.json$""")
    private val mediaFolder = Regex("""^(bg|fg)_(\d+)_(\d{8})_(\d{6})(?:_(\d+))?$""")
    private val chunk = Regex("""^chunk-stream([01])-(\d+)\.m4s(\.tmp)?$""")
    private val stamp = DateTimeFormatter.ofPattern("uuuuMMddHHmmss", Locale.ROOT).withResolverStyle(ResolverStyle.STRICT)

    /** Unknown layouts return null: they are reported, never guessed. */
    fun parseRecording(folderName: String): RecordingName? {
        recording.find(folderName)?.let { m ->
            val (kind, id, date, time) = m.destructured
            val gameId = id.toULongOrNull() ?: return null
            val utc = parseUtc(date, time) ?: return null
            return RecordingName(if (kind == "bg") RecordingKind.CacheSession else RecordingKind.Clip, gameId, utc)
        }
        return downloaded.find(folderName)?.let { m ->
            parseUtc(m.groupValues[1], m.groupValues[2])?.let { RecordingName(RecordingKind.DownloadedClip, null, it) }
        }
    }

    fun parseMediaFolder(folderName: String): MediaFolderName? {
        val m = mediaFolder.find(folderName) ?: return null
        val gameId = m.groupValues[2].toULongOrNull() ?: return null
        val utc = parseUtc(m.groupValues[3], m.groupValues[4]) ?: return null
        return MediaFolderName(m.groupValues[1] == "fg", gameId, utc, m.groupValues[5].toIntOrNull())
    }

    fun parseTimeline(fileName: String): TimelineName? {
        val m = timeline.find(fileName) ?: return null
        val gameId = m.groupValues[1].toULongOrNull() ?: return null
        return parseUtc(m.groupValues[2], m.groupValues[3])?.let { TimelineName(gameId, it) }
    }

    fun parseChunk(fileName: String): ChunkName? {
        val m = chunk.find(fileName) ?: return null
        val number = m.groupValues[2].toIntOrNull() ?: return null
        return ChunkName(m.groupValues[1].toInt(), number, m.groupValues[3].isNotEmpty())
    }

    fun chunkFile(stream: Int, number: Int): String = "chunk-stream$stream-${number.toString().padStart(5, '0')}.m4s"
    fun initFile(stream: Int): String = "init-stream$stream.m4s"

    private fun parseUtc(date: String, time: String): Instant? = try {
        LocalDateTime.parse(date + time, stamp).toInstant(ZoneOffset.UTC)
    } catch (_: DateTimeParseException) {
        null
    }
}
