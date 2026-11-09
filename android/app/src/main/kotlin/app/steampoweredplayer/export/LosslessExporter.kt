// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.SessionMedia
import java.io.File
import java.io.IOException

/**
 * Save clip: every kept range's whole chunks in one file, each chunk once, on their real timestamps; edit lists show exactly the source frames of each range. No sample is
 * touched. The files must all be on the phone already. Blocking: call off the main thread.
 */
object LosslessExporter {
    class Result(val file: File, val videoEdits: List<EditEntry>, val audioEdits: List<EditEntry>)

    /** Every chunk a Save clip of [plans] needs, as (video chunks, audio chunks). */
    fun chunksNeeded(media: SessionMedia, plans: List<ExportPlan>, audioDelay: Double): Pair<List<Int>, List<Int>> {
        val video = plans.flatMap { it.firstChunk..it.lastChunk }.filter(media::contains).distinct().sorted()
        val audio = plans.flatMap { p -> ExportPlan.audioChunks(media, p, audioDelay).let { it.first..it.second } }.filter(media::contains).distinct().sorted()
        return video to audio
    }

    /** [media].folder holds the chunks. [output] is replaced; on a failure or cancellation it's removed. */
    fun export(
        media: SessionMedia,
        spans: List<ExportSpan>,
        audioDelay: Double,
        chapters: List<ExportChapter>,
        output: File,
        cancelled: () -> Boolean = { false },
        progress: (Double) -> Unit = {},
    ): Result {
        val plans = ExportSpans.plans(media, spans)
        if (plans.isEmpty()) throw IOException("Nothing to export: this range has no chunks.")
        val (videoChunks, audioChunks) = chunksNeeded(media, plans, audioDelay)
        val folder = File(media.folder)
        val video = track(folder, 0, videoChunks, true)
        val audio = track(folder, 1, audioChunks, false)
        val (videoEdits, audioEdits) = LosslessEdits.compute(
            plans.map { LosslessEdits.rangeOf(media, it) },
            StoredPackets(video.sourceSeconds(), video.mediaTicks()),
            StoredPackets(audio.sourceSeconds(), audio.mediaTicks()),
            Mp4Remux.timescales(video, audio),
            audioDelay,
        )
        if (videoEdits.isEmpty()) throw IOException("Nothing to export: no frame falls inside this range.")
        val laidOut = ExportChapters.layout(plans, chapters, 1.0)
        try {
            Mp4Remux.write(output, video, audio, videoEdits, audioEdits, laidOut, cancelled, progress)
        } catch (e: Exception) {
            output.delete()
            throw e
        }
        return Result(output, videoEdits, audioEdits)
    }

    private fun track(folder: File, stream: Int, chunks: List<Int>, isVideo: Boolean): Mp4Track {
        val initFile = File(folder, FolderNames.initFile(stream))
        if (!initFile.exists()) throw IOException("The recording's ${if (isVideo) "video" else "audio"} setup isn't on this phone.")
        val init = InitSegment.parse(initFile.readBytes())
        val samples = chunks.flatMap { n ->
            val file = File(folder, FolderNames.chunkFile(stream, n))
            if (!file.exists()) throw IOException("Part of the recording isn't on this phone yet.")
            Fmp4Chunk.samples(file, init)
        }
        return Mp4Track(init, samples, isVideo)
    }
}
