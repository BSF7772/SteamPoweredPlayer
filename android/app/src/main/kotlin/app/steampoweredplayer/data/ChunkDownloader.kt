// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.IOException
import java.io.InputStream
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull

/** The file isn't on the share any more: Steam trimmed it. Retrying can't help. */
class RemoteFileGone(path: String) : IOException("gone: $path")

/** One way to read the library, read-only. Blocking calls; a failure is an [IOException]. */
interface RemoteFiles {
    /** The file names in a folder. Throws [RemoteFileGone] when the folder isn't there. */
    fun list(relativeFolder: String): List<String>
    fun size(relativePath: String): Long
    fun open(relativePath: String, offset: Long): InputStream
}

enum class DownloadStatus { Idle, Running, Reconnecting, CannotReach, Done }

/** The data figures the plan asks for: what came from the source so far. */
data class DownloadProgress(
    val status: DownloadStatus = DownloadStatus.Idle,
    val filesTotal: Int = 0,
    val filesDone: Int = 0,
    val filesGone: Int = 0,
    val filesFailed: Int = 0,
    val bytesFromSource: Long = 0,
    val profileIndex: Int = 0,
)

/**
 * Fetches the chunks of one recording into the cache, Steam's chunk file being the unit (plan §4.3): init segments first, then from the playhead
 * forward, then behind it; [seek] reprioritises. A dropped connection loses only the bytes in flight, because the `.part` is resumed. Reconnects follow
 * [ReconnectPolicy]: backoff, 30 s per source, the next source, then "can't reach" until [onNetworkChanged] says to try again.
 * [sources] are in failover order, such as LAN then VPN.
 */
class ChunkDownloader(
    private val cache: LocalChunkCache,
    private val libraryId: String,
    private val recording: String,
    private val relativeFolder: String,
    private val sources: List<RemoteFiles>,
    private val policy: ReconnectPolicy,
    private val io: CoroutineDispatcher,
    private val workers: Int = 2,
) {
    private val lock = Mutex()
    private val pending = LinkedHashSet<ChunkKey>()
    private val inFlight = HashSet<ChunkKey>()
    private val sizeRetries = HashMap<ChunkKey, Int>()
    private var playhead = 0
    private val networkGeneration = MutableStateFlow(0)
    private val _progress = MutableStateFlow(DownloadProgress())
    val progress: StateFlow<DownloadProgress> = _progress

    init {
        require(sources.isNotEmpty() && workers >= 1)
    }

    /** Sets what to fetch: the init segments and both streams of [chunks]. Files already cached are skipped. */
    suspend fun select(chunks: Iterable<Int>, playheadChunk: Int) = lock.withLock {
        playhead = playheadChunk
        val all = (0..1).map { key(it, 0) } + chunks.flatMap { n -> (0..1).map { key(it, n) } }
        val wanted = all.filterNot { it in cache }
        pending.clear()
        pending.addAll(wanted)
        sizeRetries.clear()
        _progress.update { DownloadProgress(filesTotal = wanted.size, profileIndex = policy.profileIndex) }
    }

    /** The playhead moved: fetch from there next. */
    suspend fun seek(playheadChunk: Int) = lock.withLock { playhead = playheadChunk }

    /** A network came up or changed: stop waiting and try now, starting again from the first source. */
    suspend fun onNetworkChanged() {
        lock.withLock { policy.onNetworkChanged() }
        networkGeneration.update { it + 1 }
    }

    /** Runs until everything selected is fetched, gone or failed. Cancel the caller's scope to stop; every `.part` stays for the next run. */
    suspend fun run() {
        _progress.update { it.copy(status = DownloadStatus.Running) }
        coroutineScope { repeat(workers) { launch { work() } } }
        _progress.update { it.copy(status = DownloadStatus.Done) }
    }

    private suspend fun work() {
        while (true) {
            val key = claim() ?: return
            try {
                awaitTurn()
                fetch(key)
                done(key) { it.copy(filesDone = it.filesDone + 1) }
            } catch (_: RemoteFileGone) {
                done(key) { it.copy(filesGone = it.filesGone + 1) }
            } catch (_: ChunkSizeException) {
                val retry = lock.withLock { (sizeRetries.merge(key, 1, Int::plus) ?: 0) < MAX_SIZE_RETRIES }
                if (retry) release(key) else done(key) { it.copy(filesFailed = it.filesFailed + 1) }
            } catch (_: IOException) {
                release(key)
                lock.withLock {
                    policy.onDisconnected()
                    policy.onAttemptFailed()
                }
            }
        }
    }

    /** The next file by the plan's order, or null when no file is left to take. */
    private suspend fun claim(): ChunkKey? = lock.withLock {
        val next = pending.filter { it.chunk == 0 }.minByOrNull { it.stream } ?: run {
            val order = ChunkPlan.order(pending.map { it.chunk }, playhead) { false }
            order.firstOrNull()?.let { n -> pending.filter { it.chunk == n }.minByOrNull { it.stream } }
        } ?: return@withLock null
        pending.remove(next)
        inFlight.add(next)
        next
    }

    private suspend fun release(key: ChunkKey) = lock.withLock {
        inFlight.remove(key)
        pending.add(key)
        Unit
    }

    private suspend fun done(key: ChunkKey, update: (DownloadProgress) -> DownloadProgress) {
        lock.withLock { inFlight.remove(key) }
        _progress.update(update)
    }

    /** Returns when it's this attempt's turn. A source that's working returns at once. */
    private suspend fun awaitTurn() {
        while (true) {
            val wait: Long? = lock.withLock {
                when (policy.state) {
                    ReconnectState.Connected -> 0L
                    ReconnectState.Reconnecting -> policy.millisUntilNextAttempt
                    ReconnectState.TimedOut -> if (policy.action == TimedOutAction.TryNextProfile) {
                        policy.tryNextProfile()
                        0L
                    } else {
                        null
                    }
                }
            }
            val generation = networkGeneration.value
            when (wait) {
                0L -> {
                    _progress.update {
                        it.copy(status = if (policy.state == ReconnectState.Connected) DownloadStatus.Running else DownloadStatus.Reconnecting, profileIndex = policy.profileIndex)
                    }
                    return
                }
                null -> {
                    _progress.update { it.copy(status = DownloadStatus.CannotReach) }
                    networkGeneration.first { it != generation }
                }
                else -> {
                    _progress.update { it.copy(status = DownloadStatus.Reconnecting) }
                    withTimeoutOrNull(wait) { networkGeneration.first { it != generation } }
                }
            }
        }
    }

    private suspend fun fetch(key: ChunkKey) {
        val source = sources[policy.profileIndex.coerceAtMost(sources.lastIndex)]
        val name = if (key.chunk == 0) FolderNames.initFile(key.stream) else FolderNames.chunkFile(key.stream, key.chunk)
        val path = "$relativeFolder/$name"
        runInterruptible(io) {
            val size = source.size(path)
            val offset = cache.partialLength(key).let { if (it in 1..size) it else 0 }
            source.open(path, offset).use { raw ->
                val counted = object : java.io.FilterInputStream(raw) {
                    override fun read(b: ByteArray, off: Int, len: Int): Int =
                        super.read(b, off, len).also { n -> if (n > 0) _progress.update { it.copy(bytesFromSource = it.bytesFromSource + n) } }
                }
                cache.put(key, counted, size, resume = offset > 0)
            }
        }
        lock.withLock { if (policy.state != ReconnectState.Connected) policy.onConnected() }
    }

    private fun key(stream: Int, chunk: Int) = ChunkKey(libraryId, recording, stream, chunk)

    private companion object {
        const val MAX_SIZE_RETRIES = 3
    }
}
