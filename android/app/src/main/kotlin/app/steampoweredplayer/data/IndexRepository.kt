// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.File
import java.io.IOException
import java.time.Instant
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

/** Where a library is, relative to the share's root, forward slashes. [dataFolder] is null when the desktop has never run on it. */
@Serializable
data class LibraryLayout(val recordingRoot: String, val dataFolder: String? = null)

/** Finds the recordings and the data folder from the profile's folder: the folder may be the share's root, either of them, or their common parent. */
object LibraryLocator {
    const val DATA_FOLDER_NAME = "SteamPoweredPlayer"
    const val LIBRARY_JSON = "library.json"
    private const val MAX_CHILDREN_LOOKED_AT = 64
    private val recordingMarkers = setOf("clips", "video", "gamerecording.pb")

    fun locate(files: RemoteFiles, profilePath: String): LibraryLayout? {
        val base = normalize(profilePath)
        val here = names(files, base) ?: return null
        val parent = if (base.isEmpty()) null else base.substringBeforeLast('/', "")
        val isRecordingRoot = here.any { it in recordingMarkers }
        return when {
            LIBRARY_JSON in here -> {
                val root = parent?.let { recordingRootAmong(files, it, setOf(lastName(base))) } ?: return null
                LibraryLayout(root, base)
            }
            isRecordingRoot -> {
                val sibling = parent?.let { join(it, DATA_FOLDER_NAME) }
                LibraryLayout(base, sibling?.takeIf { hasIndex(files, it) })
            }
            else -> {
                val data = join(base, DATA_FOLDER_NAME).takeIf { DATA_FOLDER_NAME in here && hasIndex(files, it) }
                val root = recordingRootAmong(files, base, setOf(DATA_FOLDER_NAME)) ?: return null
                LibraryLayout(root, data)
            }
        }
    }

    fun join(a: String, b: String): String = if (a.isEmpty()) b else "$a/$b"

    private fun normalize(path: String) = path.replace('\\', '/').split('/').filter { it.isNotEmpty() }.joinToString("/")

    private fun lastName(path: String) = path.substringAfterLast('/')

    private fun hasIndex(files: RemoteFiles, folder: String) = names(files, folder)?.contains(LIBRARY_JSON) == true

    private fun recordingRootAmong(files: RemoteFiles, folder: String, skip: Set<String>): String? {
        val children = names(files, folder) ?: return null
        return children.asSequence().filterNot { it in skip || it.startsWith(".") || it.contains('.') }.take(MAX_CHILDREN_LOOKED_AT)
            .map { join(folder, it) }.firstOrNull { child -> names(files, child)?.any { it in recordingMarkers } == true }
    }

    private fun names(files: RemoteFiles, folder: String): Set<String>? = try {
        files.list(folder).map { it.trimEnd('/') }.toSet()
    } catch (_: RemoteFileGone) {
        null
    }
}

enum class LibrarySource { Index, Scan, Cached, None }

/** What the library screen shows. [message] says why it isn't fresh; the index stays usable when a refresh fails. */
data class LibrarySnapshot(
    val index: LibraryIndex?,
    val source: LibrarySource,
    val status: LibraryIndexStatus,
    val layout: LibraryLayout?,
    val message: String? = null,
    /** The share couldn't be reached, which a network switch also looks like. False for answers the share gave, such as "no recordings here". */
    val unreachable: Boolean = false,
)

/**
 * The library as the phone knows it: `library.json` from the desktop's data folder when there is one, otherwise a scan of the share with ids instead of names
 * and no art. The last good result is kept in [dir], so the screen fills before the network answers and works offline. Nothing is ever written to the share.
 * Blocking: call off the main thread.
 */
class IndexRepository(
    private val dir: File,
    private val files: RemoteFiles,
    private val profilePath: String,
    private val clock: () -> Instant = Instant::now,
    private val pause: (Long) -> Unit = { Thread.sleep(it) },
) {
    private val json = Json { ignoreUnknownKeys = true }
    private val layoutFile get() = File(dir, "layout.json")
    private val indexFile get() = File(dir, LibraryLocator.LIBRARY_JSON)
    private val sourceFile get() = File(dir, "source.txt")

    /** The last index kept on the phone, or null. Reads local files only. */
    fun cached(): LibrarySnapshot? {
        val parsed = try {
            LibraryIndex.parse(indexFile.takeIf { it.exists() }?.readBytes() ?: return null)
        } catch (_: IOException) {
            return null
        }
        val index = parsed.index ?: return null
        return LibrarySnapshot(index, LibrarySource.Cached, parsed.status, storedLayout())
    }

    /** Refreshes from the share. Without a connection this is [cached] with the reason in [LibrarySnapshot.message]. */
    fun refresh(): LibrarySnapshot {
        val layout = try {
            storedLayout()?.takeIf { stillThere(it) } ?: LibraryLocator.locate(files, profilePath)
        } catch (e: IOException) {
            return offline(e)
        } ?: return LibrarySnapshot(null, LibrarySource.None, LibraryIndexStatus.Missing, null, "No recordings found in this folder of the share.")
        store(layoutFile, json.encodeToString(LibraryLayout.serializer(), layout).toByteArray())

        return try {
            readIndex(layout) ?: scan(layout)
        } catch (e: IOException) {
            offline(e)
        }
    }

    private fun stillThere(layout: LibraryLayout): Boolean = try {
        files.list(layout.recordingRoot).isNotEmpty()
    } catch (_: RemoteFileGone) {
        false
    }

    private fun readIndex(layout: LibraryLayout): LibrarySnapshot? {
        val folder = layout.dataFolder ?: return null
        val bytes = readWithRetries(LibraryLocator.join(folder, LibraryLocator.LIBRARY_JSON)) ?: return null
        val parsed = LibraryIndex.parse(bytes)
        val index = parsed.index ?: return null                                       // corrupt, or a newer shape: the scan still gives a library
        store(indexFile, bytes)
        store(sourceFile, "index".toByteArray())
        return LibrarySnapshot(safe(index), LibrarySource.Index, parsed.status, layout)
    }

    /** The writer swaps the file by rename, so a reader can see "not found" or a sharing error for a moment (docs/data-folder.md, up to 3 and 10 tries, 20 ms apart). */
    private fun readWithRetries(path: String): ByteArray? {
        var notFound = 0
        var errors = 0
        while (true) {
            try {
                return files.open(path, 0).use { it.readBytes() }
            } catch (_: RemoteFileGone) {
                if (++notFound >= NOT_FOUND_TRIES) return null
            } catch (e: IOException) {
                if (++errors >= OTHER_TRIES) throw e
            }
            pause(RETRY_MILLIS)
        }
    }

    private fun scan(layout: LibraryLayout): LibrarySnapshot {
        val previous = cached()?.index
        val index = LibraryScan.scan(files, layout.recordingRoot, previous?.libraryId ?: "", clock())
        store(indexFile, LibraryScan.encode(index))
        store(sourceFile, "scan".toByteArray())
        return LibrarySnapshot(index, LibrarySource.Scan, LibraryIndexStatus.Missing, layout)
    }

    private fun safe(index: LibraryIndex) = index.copy(recordings = index.recordings.filterNot { r -> r.relativeFolder.split('/').any { it == ".." || it.isEmpty() } })

    private fun offline(e: IOException): LibrarySnapshot {
        val kept = cached()
        return (kept ?: LibrarySnapshot(null, LibrarySource.None, LibraryIndexStatus.Missing, null)).copy(message = e.message ?: e.javaClass.simpleName, unreachable = true)
    }

    private fun storedLayout(): LibraryLayout? = try {
        layoutFile.takeIf { it.exists() }?.let { json.decodeFromString(LibraryLayout.serializer(), it.readText()) }
    } catch (_: SerializationException) {
        null
    } catch (_: IOException) {
        null
    }

    private fun store(file: File, bytes: ByteArray) {
        file.parentFile?.mkdirs()
        val tmp = File(file.path + ".tmp")
        tmp.writeBytes(bytes)
        if (!tmp.renameTo(file)) {
            file.delete()
            tmp.renameTo(file)
        }
    }

    private companion object {
        const val NOT_FOUND_TRIES = 3
        const val OTHER_TRIES = 10
        const val RETRY_MILLIS = 20L
    }
}

/** The library without the desktop: folder listings only, so ids instead of names and no art. One listing per recording; no chunk head is read here. */
object LibraryScan {
    fun scan(files: RemoteFiles, recordingRoot: String, libraryId: String, now: Instant): LibraryIndex {
        val recordings = ArrayList<RecordingEntry>()
        for (name in names(files, LibraryLocator.join(recordingRoot, "video"))) {
            val parsed = FolderNames.parseRecording(name)?.takeIf { it.kind == RecordingKind.CacheSession } ?: continue
            val folder = LibraryLocator.join(LibraryLocator.join(recordingRoot, "video"), name)
            entry(files, folder, "video/$name", parsed, null)?.let(recordings::add)
        }
        for (name in names(files, LibraryLocator.join(recordingRoot, "clips"))) {
            val parsed = FolderNames.parseRecording(name)?.takeIf { it.kind != RecordingKind.CacheSession } ?: continue
            val clip = LibraryLocator.join(LibraryLocator.join(recordingRoot, "clips"), name)
            val pb = readClipPb(files, clip)
            val media = RecordingFolders.clipMediaFolder(files, clip, pb?.mediaFolders?.firstOrNull()) ?: continue
            entry(files, media, "clips/$name", parsed, pb)?.let(recordings::add)
        }
        recordings.sortWith(compareByDescending<RecordingEntry> { it.startUtc }.thenBy { it.relativeFolder })
        val games = recordings.mapNotNull { it.gameId }.distinct().sortedBy { it.toULongOrNull() }.map { GameEntry(it, "App $it") }
        return LibraryIndex(LibraryIndex.CURRENT_SCHEMA_VERSION, libraryId, now, games, recordings)
    }

    fun encode(index: LibraryIndex): ByteArray = json.encodeToString(LibraryIndex.serializer(), index).toByteArray()

    private val json = Json { encodeDefaults = false }

    private fun entry(files: RemoteFiles, folder: String, relative: String, name: RecordingName, pb: ClipPb?): RecordingEntry? {
        val listing = names(files, folder)
        val chunks = ChunkScan.complete(listing) { HeadCheck.Valid }                 // heads are read when the recording is opened
        if (chunks.isEmpty()) return null
        val segment = SessionMedia.DEFAULT_SEGMENT_SECONDS
        val kind = when (name.kind) {
            RecordingKind.CacheSession -> WireKind.Cache
            RecordingKind.Clip -> WireKind.Clip
            RecordingKind.DownloadedClip -> WireKind.DownloadedClip
        }
        return RecordingEntry(
            kind, name.gameId?.toString(), name.startUtc, relative, SessionMedia.runsOf(chunks), segment, chunks.size * segment,
            temporary = name.kind == RecordingKind.CacheSession, title = pb?.title?.takeIf { it.isNotBlank() }, exactOffsetMs = pb?.offsetMs,
        )
    }

    private fun readClipPb(files: RemoteFiles, clip: String): ClipPb? = try {
        files.open(LibraryLocator.join(clip, "clip.pb"), 0).use { RecordingPb.parseClip(it.readBytes()) }
    } catch (_: RemoteFileGone) {
        null
    }

    private fun names(files: RemoteFiles, folder: String): List<String> = try {
        files.list(folder).map { it.trimEnd('/') }
    } catch (_: RemoteFileGone) {
        emptyList()
    }
}
