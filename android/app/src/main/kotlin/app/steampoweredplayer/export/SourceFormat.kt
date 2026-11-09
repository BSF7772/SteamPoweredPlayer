// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import android.media.MediaExtractor
import android.media.MediaFormat
import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.SessionMedia
import app.steampoweredplayer.edit.ConcatDataSource
import java.io.File
import java.io.IOException

/** The picture's size and frame rate, from the recording's own first chunk. Blocking. */
object SourceFormat {
    data class Result(val width: Int, val height: Int, val fps: Double)

    fun read(media: SessionMedia): Result {
        val folder = File(media.folder)
        val first = media.chunks().firstOrNull { File(folder, FolderNames.chunkFile(0, it)).exists() } ?: throw IOException("No chunk of this recording is on the phone.")
        val init = File(folder, FolderNames.initFile(0))
        val chunk = File(folder, FolderNames.chunkFile(0, first))
        val source = ConcatDataSource(listOf(init, chunk))
        val extractor = MediaExtractor()
        try {
            extractor.setDataSource(source)
            val format = (0 until extractor.trackCount).map(extractor::getTrackFormat).firstOrNull { it.getString(MediaFormat.KEY_MIME)!!.startsWith("video/") }
                ?: throw IOException("No picture in this recording.")
            val parsed = InitSegment.parse(init.readBytes())
            val times = Fmp4Chunk.samples(chunk, parsed).map { it.dts }
            val fps = if (times.size > 1) (times.size - 1).toDouble() * parsed.timescale / (times.last() - times.first()) else 60.0
            return Result(format.getInteger(MediaFormat.KEY_WIDTH), format.getInteger(MediaFormat.KEY_HEIGHT), fps)
        } finally {
            extractor.release()
            source.close()
        }
    }
}
