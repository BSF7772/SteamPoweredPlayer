// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.File
import java.io.IOException
import java.time.Instant
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

/** The name a recording goes by on the phone: its folder's name (`bg_*`, `clip_*`). Also the desktop's trickplay key. */
val RecordingEntry.key: String get() = relativeFolder.trimEnd('/').substringAfterLast('/')

object RecordingFolders {
    /**
     * The folder with a clip's chunks, `<clip>/video/<media folder>`, as in the desktop's `FindClipMediaFolder`: the one `clip.pb` names; else a known name,
     * the newest by name; else any subfolder holding `init-stream0.m4s`.
     */
    fun clipMediaFolder(files: RemoteFiles, clip: String, recordingId: String?): String? {
        val video = LibraryLocator.join(clip, "video")
        val children = try {
            files.list(video).map { it.trimEnd('/') }
        } catch (_: RemoteFileGone) {
            return null
        }
        if (recordingId != null && isPlainName(recordingId) && recordingId in children) return LibraryLocator.join(video, recordingId)
        children.filter { FolderNames.parseMediaFolder(it) != null }.maxOrNull()?.let { return LibraryLocator.join(video, it) }
        return children.firstOrNull { child ->
            try {
                FolderNames.initFile(0) in files.list(LibraryLocator.join(video, child))
            } catch (_: RemoteFileGone) {
                false
            }
        }?.let { LibraryLocator.join(video, it) }
    }
}

/** What opening a recording learned from the share, kept on the phone so it opens offline. Paths are relative to the share's root. */
@Serializable
data class RecordingDetails(
    val key: String,
    val mediaFolder: String,
    val runs: List<@Serializable(SegmentRunSerializer::class) SegmentRun>,
    val segmentSeconds: Double,
    val axisShiftSeconds: Double,
    val startLimitSeconds: Double? = null,
    val endLimitSeconds: Double? = null,
    val exactOffsetMs: Long? = null,
    val bgStartEpochSeconds: Long? = null,
    val timelineStartEpochSeconds: Long? = null,
    val hasTimeline: Boolean = false,
    val hasTrickplay: Boolean = false,
) {
    /** The recording's media as if it all were in [localFolder]. */
    fun media(localFolder: String) = SessionMedia(localFolder, runs, segmentSeconds, axisShiftSeconds, endLimitSeconds, startLimitSeconds)
}

/**
 * Per-recording facts: the media folder, complete chunks, the axis shift from the first chunk's own tfdt, a clip's bounds, markers and the trickplay sheets.
 * Reads the share read-only; writes only under [dir]. Blocking: call off the main thread.
 */
class RecordingRepository(private val dir: File, private val files: RemoteFiles?, private val layout: LibraryLayout?) {
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    private fun folder(key: String) = File(dir, key).also { require(isPlainName(key)) }
    private fun detailsFile(key: String) = File(folder(key), "details.json")
    private fun timelineFile(key: String) = File(folder(key), "timeline.json")
    fun trickDir(key: String) = File(folder(key), "trick")

    fun stored(key: String): RecordingDetails? = try {
        detailsFile(key).takeIf { it.exists() }?.let { json.decodeFromString(RecordingDetails.serializer(), it.readText()) }
    } catch (_: SerializationException) {
        null
    } catch (_: IOException) {
        null
    }

    /**
     * Refreshes from the share. [checkHeads] reads each chunk's head: the desktop's index has done that already, a fallback scan has not.
     * Throws [IOException] when the share can't be read and [RemoteFileGone] when the recording is gone.
     */
    fun load(entry: RecordingEntry, checkHeads: Boolean, head: ((List<String>) -> Map<String, HeadCheck>)? = null): RecordingDetails {
        val remote = files ?: throw IOException("not connected")
        val root = layout?.recordingRoot ?: throw IOException("no library layout")
        val relative = LibraryLocator.join(root, entry.relativeFolder)
        val isClip = entry.kind != RecordingKind.CacheSession
        val clipPb = if (isClip) read(remote, LibraryLocator.join(relative, "clip.pb"))?.let(RecordingPb::parseClip) else null
        val media = if (isClip) {
            RecordingFolders.clipMediaFolder(remote, relative, clipPb?.mediaFolders?.firstOrNull()) ?: throw RemoteFileGone(entry.key)
        } else {
            relative
        }
        val names = remote.list(media).map { it.trimEnd('/') }
        val named = SessionMedia.runsOf(ChunkScan.complete(names) { HeadCheck.Valid })
        val runs = if (checkHeads) {
            val candidates = ChunkScan.headCandidates(names)
            val checks = head?.invoke(candidates.map { LibraryLocator.join(media, it) })?.mapKeys { it.key.substringAfterLast('/') }
            SessionMedia.runsOf(ChunkScan.complete(names) { name -> checks?.get(name) ?: headOf(remote, LibraryLocator.join(media, name)) })
        } else {
            SessionMedia.intersect(entry.runs, named)                                 // Steam may have trimmed it since the index was written
        }
        val segment = entry.segmentSeconds.takeIf { it > 0 } ?: SessionMedia.DEFAULT_SEGMENT_SECONDS
        var shift = 0.0
        runs.firstOrNull()?.first?.let { first ->
            val init = readPart(remote, LibraryLocator.join(media, FolderNames.initFile(0)), MAX_INIT_BYTES)
            val chunkHead = readPart(remote, LibraryLocator.join(media, FolderNames.chunkFile(0, first)), FirstFragment.TFDT_HEAD_BYTES)
            if (init != null && chunkHead != null) FirstFragment.tfdtSeconds(init, chunkHead)?.let { shift = SessionMedia.axisShiftFor(it, first, segment) }
        }
        var base = SessionMedia(media, runs, segment, shift)
        if (isClip) base = ClipBounds.apply(base, readPart(remote, LibraryLocator.join(media, "session.mpd"), MPD_BYTES)?.toString(Charsets.UTF_8))

        val mediaName = media.substringAfterLast('/')
        val bgStart = FolderNames.parseMediaFolder(mediaName)?.startUtc ?: entry.startUtc
        var offset: Long?
        var timelineName: String?
        var timelineBytes: ByteArray? = null
        if (isClip) {
            offset = clipPb?.offsetMsForFolder(mediaName) ?: clipPb?.offsetMs
            val folder = LibraryLocator.join(relative, "timelines")
            timelineName = listOrEmpty(remote, folder).filter { it.endsWith(".json") }.minOrNull()
            timelineName?.let { timelineBytes = read(remote, LibraryLocator.join(folder, it)) }
        } else {
            val pb = read(remote, LibraryLocator.join(root, "gamerecording.pb"))?.let(RecordingPb::parseGameRecording)
            offset = pb?.offsetMsForFolder(mediaName) ?: entry.exactOffsetMs
            val folder = LibraryLocator.join(root, "timelines")
            val listed = listOrEmpty(remote, folder).filter { it.startsWith("timeline_") && it.endsWith(".json") }
            timelineName = cacheTimeline(pb, listed, mediaName, entry)
            timelineName?.let { timelineBytes = read(remote, LibraryLocator.join(folder, it)) }
        }
        val details = RecordingDetails(
            entry.key, media, base.runs, segment, shift, base.startLimitSeconds, base.endLimitSeconds, offset, bgStart.epochSecond,
            timelineName?.let { FolderNames.parseTimeline(it)?.startUtc?.epochSecond }, timelineBytes != null, loadTrickplay(remote, entry.key),
        )
        store(timelineFile(entry.key), timelineBytes)
        store(detailsFile(entry.key), json.encodeToString(RecordingDetails.serializer(), details).toByteArray())
        return details
    }

    /** The markers on the media axis, from what [load] stored. */
    fun markers(details: RecordingDetails): MarkerSet {
        val bytes = try {
            timelineFile(details.key).takeIf { it.exists() }?.readBytes()
        } catch (_: IOException) {
            null
        } ?: return MarkerSet(emptyList(), MarkerAlignment.None, emptyList())
        return Markers.map(
            Timeline.parse(bytes), details.exactOffsetMs, details.bgStartEpochSeconds?.let(Instant::ofEpochSecond),
            details.timelineStartEpochSeconds?.let(Instant::ofEpochSecond),
        )
    }

    /** The trickplay manifest stored by [load], and the folder with its sheets. */
    fun trickplay(key: String): TrickplayManifest? = try {
        File(trickDir(key), TrickplayManifest.FILE_NAME).takeIf { it.exists() }?.readText()?.let(TrickplayManifest::parse)
    } catch (_: IOException) {
        null
    }

    private fun loadTrickplay(remote: RemoteFiles, key: String): Boolean {
        val data = layout?.dataFolder ?: return false
        val folder = LibraryLocator.join(LibraryLocator.join(data, "trick"), key)
        val local = trickDir(key)
        val bytes = read(remote, LibraryLocator.join(folder, TrickplayManifest.FILE_NAME)) ?: return local.exists()
        val manifest = TrickplayManifest.parse(bytes.toString(Charsets.UTF_8)) ?: return false
        for (sheet in manifest.sheets) {
            if (!isPlainName(sheet.file)) continue
            val path = LibraryLocator.join(folder, sheet.file)
            val target = File(local, sheet.file)
            val size = try {
                remote.size(path)
            } catch (_: RemoteFileGone) {
                continue
            }
            if (target.exists() && target.length() == size) continue
            read(remote, path)?.let { store(target, it) }
        }
        store(File(local, TrickplayManifest.FILE_NAME), bytes)
        return true
    }

    /** The timeline of a session, as on the desktop: the one `gamerecording.pb` names, else the same game's latest start at or before the session's. */
    private fun cacheTimeline(pb: GameRecordingPb?, listed: List<String>, mediaName: String, entry: RecordingEntry): String? {
        val byPb = pb?.timelines?.firstOrNull { t -> t.recordings.any { it.folder == mediaName } }?.timelineId
        if (byPb != null) listed.firstOrNull { it == "timeline_$byPb.json" }?.let { return it }
        val game = entry.gameId?.toULongOrNull() ?: return null
        return listed.mapNotNull { n -> FolderNames.parseTimeline(n)?.let { n to it } }
            .filter { (_, t) -> t.gameId == game && !t.startUtc.isAfter(entry.startUtc) }
            .maxByOrNull { (_, t) -> t.startUtc }?.first
    }

    private fun headOf(remote: RemoteFiles, path: String): HeadCheck = try {
        readPart(remote, path, ChunkHeads.HEAD_LENGTH)?.let(ChunkHeads::classify) ?: HeadCheck.Vanished
    } catch (_: IOException) {
        HeadCheck.Unreadable
    }

    private fun read(remote: RemoteFiles, path: String): ByteArray? = try {
        remote.open(path, 0).use { it.readBytes() }
    } catch (_: RemoteFileGone) {
        null
    }

    private fun readPart(remote: RemoteFiles, path: String, max: Int): ByteArray? = try {
        remote.open(path, 0).use { it.readNBytesCompat(max) }
    } catch (_: RemoteFileGone) {
        null
    }

    private fun listOrEmpty(remote: RemoteFiles, folder: String): List<String> = try {
        remote.list(folder).map { it.trimEnd('/') }
    } catch (_: RemoteFileGone) {
        emptyList()
    }

    private fun store(file: File, bytes: ByteArray?) {
        if (bytes == null) {
            file.delete()
            return
        }
        file.parentFile?.mkdirs()
        val tmp = File(file.path + ".tmp")
        tmp.writeBytes(bytes)
        if (!tmp.renameTo(file)) {
            file.delete()
            tmp.renameTo(file)
        }
    }

    private companion object {
        const val MAX_INIT_BYTES = 64 * 1024
        const val MPD_BYTES = 8192
    }
}

/** `InputStream.readNBytes` needs API 33; the app's minimum is 29. */
internal fun java.io.InputStream.readNBytesCompat(max: Int): ByteArray {
    val buffer = ByteArray(max)
    var read = 0
    while (read < max) {
        val n = read(buffer, read, max - read)
        if (n <= 0) break
        read += n
    }
    return if (read == max) buffer else buffer.copyOf(read)
}
