// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.Closeable
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

/** One downloaded file: [chunk] ≥ 1 is a chunk, 0 is the init segment. [libraryId] and [recording] become folder names, so they must be plain. */
data class ChunkKey(val libraryId: String, val recording: String, val stream: Int, val chunk: Int) {
    init {
        require(isPlainName(libraryId) && isPlainName(recording)) { "not a plain folder name" }
        require(stream in 0..1 && chunk >= 0)
    }
}

/** The stream ended short of, or ran past, the expected size. The connection itself may be fine. */
class ChunkSizeException(message: String) : IOException(message)

private val plainName = Regex("^[A-Za-z0-9._-]+$")
private val initName = Regex("""^init-stream([01])\.m4s$""")

fun isPlainName(name: String): Boolean = name != "." && name != ".." && plainName.matches(name)

/**
 * The phone's own cache of downloaded chunks, as in the desktop's `Core/Net/LocalChunkCache`: `<dir>/<libraryId>/<recording>/<Steam's file name>`,
 * so cached files go to the player as they are. Writes go to `.part`, are size-checked and renamed. LRU eviction to a byte cap never takes pinned
 * recordings, leased chunks or the entry just written, so the cache may exceed its cap. The files are the truth; the index holds only the order and
 * the pins. Unlike the desktop's, a leftover `.part` stays, because a dropped download resumes from it ([partialLength], spike S3). Thread-safe.
 */
class LocalChunkCache private constructor(private val dir: File, val maxBytes: Long) {
    @Serializable private data class IndexEntry(val library: String, val recording: String, val stream: Int, val chunk: Int, val seq: Long)
    @Serializable private data class IndexPin(val library: String, val recording: String)
    @Serializable private data class Index(val clock: Long = 0, val entries: List<IndexEntry> = emptyList(), val pinned: List<IndexPin> = emptyList())

    private data class Entry(val size: Long, val seq: Long)

    private val lock = Any()
    private val entries = HashMap<ChunkKey, Entry>()
    private val pinned = HashSet<Pair<String, String>>()
    private val inUse = HashMap<ChunkKey, Int>()
    private var clock = 0L
    private var total = 0L

    val totalBytes: Long get() = synchronized(lock) { total }
    val count: Int get() = synchronized(lock) { entries.size }

    operator fun contains(key: ChunkKey): Boolean = synchronized(lock) { key in entries }

    /** The path of a complete file, or null. Counts as a use. A file that vanished is forgotten. */
    fun pathOf(key: ChunkKey): File? = synchronized(lock) {
        val e = entries[key] ?: return null
        val f = fileOf(key)
        if (!f.exists()) {
            entries.remove(key)
            total -= e.size
            return null
        }
        entries[key] = e.copy(seq = ++clock)
        f
    }

    /** Bytes already in this file's `.part`, for resuming after a dropped connection. */
    fun partialLength(key: ChunkKey): Long = partOf(key).let { if (it.exists()) it.length() else 0 }

    /**
     * Writes [content] to the file for [key], appending to its `.part` when [resume] is set, and completes it when it reaches exactly
     * [expectedSize]. A dropped read throws and keeps the `.part` for the retry; a wrong size deletes it and throws [IOException].
     */
    fun put(key: ChunkKey, content: InputStream, expectedSize: Long, resume: Boolean = false): File {
        require(expectedSize >= 0)
        val path = fileOf(key)
        val part = partOf(key)
        path.parentFile?.mkdirs()
        var written = if (resume && part.exists()) part.length() else 0L
        java.io.FileOutputStream(part, written > 0).use { out ->
            val buffer = ByteArray(BUFFER_BYTES)
            while (written <= expectedSize) {
                val want = minOf(buffer.size.toLong(), expectedSize + 1 - written).toInt()   // one byte past the end exposes oversize
                val n = content.read(buffer, 0, want)
                if (n < 0) break
                out.write(buffer, 0, n)
                written += n
            }
            out.flush()
            out.fd.sync()
        }
        if (written != expectedSize) {
            part.delete()
            throw ChunkSizeException("expected $expectedSize bytes for ${path.name}, got ${if (written > expectedSize) "more" else written.toString()}")
        }
        synchronized(lock) {
            if (!part.renameTo(path)) {
                path.delete()
                if (!part.renameTo(path)) throw IOException("couldn't complete ${path.name}")
            }
            entries[key]?.let { total -= it.size }
            entries[key] = Entry(expectedSize, ++clock)
            total += expectedSize
            evict(key)
        }
        saveIndex()
        return path
    }

    fun pin(libraryId: String, recording: String) {
        require(isPlainName(libraryId) && isPlainName(recording))
        synchronized(lock) { pinned.add(libraryId to recording) }
        saveIndex()
    }

    fun unpin(libraryId: String, recording: String) {
        val removed = synchronized(lock) { pinned.remove(libraryId to recording) }
        if (removed) saveIndex()
    }

    fun isPinned(libraryId: String, recording: String): Boolean = synchronized(lock) { (libraryId to recording) in pinned }

    /** Not evicted until the lease is closed. Leases are counted and not persisted. */
    fun markInUse(key: ChunkKey): Closeable {
        synchronized(lock) { inUse[key] = (inUse[key] ?: 0) + 1 }
        val released = AtomicBoolean(false)
        return Closeable {
            if (released.compareAndSet(false, true)) synchronized(lock) {
                val n = (inUse[key] ?: 0) - 1
                if (n <= 0) inUse.remove(key) else inUse[key] = n
            }
        }
    }

    fun flush() = saveIndex()

    private fun evict(keep: ChunkKey?): Boolean {
        var any = false
        val undeletable = HashSet<ChunkKey>()
        while (total > maxBytes) {
            val victim = entries.entries
                .filter { (k, _) -> k != keep && k !in undeletable && (k.libraryId to k.recording) !in pinned && k !in inUse }
                .minByOrNull { it.value.seq } ?: break
            if (!fileOf(victim.key).delete() && fileOf(victim.key).exists()) {
                undeletable.add(victim.key)
                continue
            }
            total -= victim.value.size
            entries.remove(victim.key)
            any = true
        }
        return any
    }

    private fun scan(index: Index?) {
        val seqs = HashMap<ChunkKey, Long>()
        if (index != null) {
            clock = index.clock
            for (e in index.entries) keyOrNull(e.library, e.recording, e.stream, e.chunk)?.let { seqs[it] = e.seq }
            for (p in index.pinned) if (isPlainName(p.library) && isPlainName(p.recording)) pinned.add(p.library to p.recording)
        }
        for (lib in dir.listFiles().orEmpty()) {
            if (!lib.isDirectory || !isPlainName(lib.name)) continue
            for (rec in lib.listFiles().orEmpty()) {
                if (!rec.isDirectory || !isPlainName(rec.name)) continue
                for (file in rec.listFiles().orEmpty()) {
                    if (file.name.endsWith(PART_SUFFIX)) continue       // kept: the download resumes from it
                    val (stream, chunk) = parseFileName(file.name) ?: continue
                    val key = ChunkKey(lib.name, rec.name, stream, chunk)
                    val size = file.length()
                    entries[key] = Entry(size, seqs[key] ?: 0)
                    total += size
                }
            }
        }
        clock = maxOf(clock, entries.values.maxOfOrNull { it.seq } ?: 0)
        File(dir, "$INDEX_FILE.tmp").delete()
    }

    private fun keyOrNull(library: String, recording: String, stream: Int, chunk: Int): ChunkKey? =
        if (isPlainName(library) && isPlainName(recording) && stream in 0..1 && chunk >= 0) ChunkKey(library, recording, stream, chunk) else null

    private fun parseFileName(name: String): Pair<Int, Int>? {
        initName.find(name)?.let { return it.groupValues[1].toInt() to 0 }
        return FolderNames.parseChunk(name)?.takeUnless { it.temporary }?.let { it.stream to it.number }
    }

    private fun loadIndex(): Index? = try {
        File(dir, INDEX_FILE).takeIf { it.exists() }?.let { json.decodeFromString<Index>(it.readText()) }
    } catch (_: SerializationException) {
        null                                                            // corrupt: the disk scan alone rebuilds the cache
    } catch (_: IOException) {
        null
    }

    private val saving = Any()

    private fun saveIndex() = synchronized(saving) {
        val snapshot = synchronized(lock) {
            Index(
                clock,
                entries.map { (k, e) -> IndexEntry(k.libraryId, k.recording, k.stream, k.chunk, e.seq) },
                pinned.map { (l, r) -> IndexPin(l, r) },
            )
        }
        val tmp = File(dir, "$INDEX_FILE.tmp")
        val final = File(dir, INDEX_FILE)
        tmp.writeText(json.encodeToString(snapshot))
        if (!tmp.renameTo(final)) {
            final.delete()
            tmp.renameTo(final)
        }
        Unit
    }

    private fun fileOf(key: ChunkKey) =
        File(File(File(dir, key.libraryId), key.recording), if (key.chunk == 0) FolderNames.initFile(key.stream) else FolderNames.chunkFile(key.stream, key.chunk))

    private fun partOf(key: ChunkKey) = File(fileOf(key).path + PART_SUFFIX)

    companion object {
        private const val INDEX_FILE = "cache-index.json"
        private const val PART_SUFFIX = ".part"
        private const val BUFFER_BYTES = 81920
        private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

        /** Opens (creating if needed), reads the files and bookkeeping, and trims to [maxBytes]. Blocking: call off the main thread. */
        fun open(dir: File, maxBytes: Long): LocalChunkCache {
            require(maxBytes >= 0)
            dir.mkdirs()
            val cache = LocalChunkCache(dir, maxBytes)
            val index = cache.loadIndex()
            cache.scan(index)
            val trimmed = synchronized(cache.lock) { cache.evict(null) }
            if (trimmed || index == null) cache.saveIndex()
            return cache
        }
    }
}
