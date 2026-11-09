// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import android.graphics.Bitmap
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMetadataRetriever
import app.steampoweredplayer.data.FolderNames
import java.io.File
import java.io.IOException
import kotlin.math.abs

/**
 * One frame of a recording as a full-size picture, for a screenshot. The libmpv build on the phone carries no image encoders, so the frame is decoded again from the chunk on
 * the phone by Android's own decoder and saved by Android's own PNG encoder.
 *
 * The frame is found by its place in display order inside its chunk (from the chunk's own `moof` table, as frame back does), never by a time handed to the platform: the
 * platform's extractor keeps its own clock, so its sample times are read back from the same file and the k-th one is asked for.
 */
object FrameGrab {
    /** Where a frame sits: the chunk that stores it and its index in that chunk's frames sorted by presentation time. */
    data class Place(val chunk: Int, val index: Int, val seconds: Double)

    /**
     * The frame on screen at [position]: the nearest frame within a quarter of a frame, else the last one before it. [tables] are the frame times of the chunks around the
     * playhead, each sorted; a chunk's frames can run a little past its 3 s slot, so the neighbours are searched too. Pure.
     */
    fun pick(tables: Map<Int, DoubleArray>, position: Double, frameSeconds: Double): Place? {
        var best: Place? = null
        var bestD = Double.MAX_VALUE
        var before: Place? = null
        for ((chunk, times) in tables) {
            times.forEachIndexed { i, t ->
                val d = abs(t - position)
                if (d < bestD) {
                    bestD = d
                    best = Place(chunk, i, t)
                }
                if (t <= position && (before == null || t > before!!.seconds)) before = Place(chunk, i, t)
            }
        }
        return if (bestD <= 0.25 * frameSeconds) best else before ?: best
    }

    /**
     * Decodes [place] from the chunk files in [folder] at the source's own size. [scratch] holds the chunk for a moment as a plain MP4, written the way Save clip writes
     * (samples untouched): the platform's extractor read no samples at all from some of Steam's fragments when given init + chunk as they are (measured on the emulator).
     */
    fun decode(folder: File, place: Place, scratch: File): Bitmap {
        val video = track(folder, 0, place.chunk, true) ?: throw IOException("This part of the recording isn't on the phone.")
        val audio = track(folder, 1, place.chunk, false) ?: throw IOException("This part of the recording isn't on the phone.")
        val source = video.sourceSeconds()
        val whole = LosslessRange(source.min(), source.max() + 1.0, openHead = true, openTail = true)
        val (videoEdits, audioEdits) = LosslessEdits.compute(
            listOf(whole), StoredPackets(source, video.mediaTicks()), StoredPackets(audio.sourceSeconds(), audio.mediaTicks()), Mp4Remux.timescales(video, audio), 0.0,
        )
        try {
            Mp4Remux.write(scratch, video, audio, videoEdits, audioEdits)
            val times = displayTimesUs(scratch)
            val at = times.getOrNull(place.index) ?: throw IOException("The phone read ${times.size} frames from this chunk, not the frame asked for.")
            val retriever = MediaMetadataRetriever()
            try {
                retriever.setDataSource(scratch.path)
                return retriever.getFrameAtTime(at, MediaMetadataRetriever.OPTION_CLOSEST) ?: throw IOException("The phone couldn't decode this frame.")
            } finally {
                retriever.release()
            }
        } finally {
            scratch.delete()
        }
    }

    private fun track(folder: File, stream: Int, chunk: Int, isVideo: Boolean): Mp4Track? {
        val init = File(folder, FolderNames.initFile(stream))
        val file = File(folder, FolderNames.chunkFile(stream, chunk))
        if (!init.isFile || !file.isFile) return null
        val parsed = InitSegment.parse(init.readBytes())
        return Mp4Track(parsed, Fmp4Chunk.samples(file, parsed), isVideo)
    }

    /** The video samples' times on the platform's own clock, in display order. */
    private fun displayTimesUs(file: File): List<Long> {
        val extractor = MediaExtractor()
        try {
            extractor.setDataSource(file.path)
            val track = (0 until extractor.trackCount).firstOrNull { extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("video/") == true }
                ?: throw IOException("No picture in this chunk.")
            extractor.selectTrack(track)
            val times = ArrayList<Long>()
            while (true) {
                val t = extractor.sampleTime
                if (t < 0) break
                times.add(t)
                if (!extractor.advance()) break
            }
            times.sort()
            return times
        } finally {
            extractor.release()
        }
    }
}
