// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.net

import android.content.Context
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkManager
import androidx.work.workDataOf
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.data.ChunkDownloader
import app.steampoweredplayer.data.DownloadProgress
import app.steampoweredplayer.data.LocalChunkCache
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

/** The app's download queue and the state the screens read. */
object Downloads {
    const val TAG_ALL = "download"
    private const val PREFS = "downloads"
    private const val KEY_NOTICE_DISMISSED = "metered_notice_dismissed"
    private const val GIGABYTE = 1024L * 1024 * 1024

    private val _meteredNotice = MutableStateFlow(false)
    val meteredNotice: StateFlow<Boolean> = _meteredNotice

    private val _progress = MutableStateFlow<Map<String, DownloadProgress>>(emptyMap())

    /** What each running job has done, by recording. */
    val progress: StateFlow<Map<String, DownloadProgress>> = _progress

    private val running = ConcurrentHashMap<String, ChunkDownloader>()

    @Volatile private var cache: LocalChunkCache? = null

    /** The one chunk cache, opened on first use. Blocking: call off the main thread. */
    fun cache(context: Context): LocalChunkCache = cache ?: synchronized(this) {
        cache ?: LocalChunkCache.open(File(context.applicationContext.noBackupFilesDir, "chunks"), AppSettings(context).cacheGigabytes * GIGABYTE).also { cache = it }
    }

    fun setCacheGigabytes(context: Context, gigabytes: Int) {
        AppSettings(context).cacheGigabytes = gigabytes
        cache(context).maxBytes = gigabytes * GIGABYTE
    }

    /**
     * Fetches [request]: into the job already running for that recording when there is one, else as a job of its own. A job survives the app closing and a reboot.
     */
    suspend fun fetch(context: Context, request: DownloadRequest) {
        val active = running[request.recording]
        if (active != null && request.chunks != null && active.add(request.chunks, request.playheadChunk)) return
        enqueue(context, request)
    }

    /** The playhead moved: fetch from there next. */
    suspend fun seek(recording: String, playheadChunk: Int) {
        running[recording]?.seek(playheadChunk)
    }

    fun enqueue(context: Context, request: DownloadRequest) {
        val work = OneTimeWorkRequestBuilder<DownloadWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
            .setInputData(workDataOf(DownloadWorker.KEY_REQUEST to request.encode()))
            .addTag(TAG_ALL)
            .addTag(recordingTag(request.recording))
            .build()
        WorkManager.getInstance(context).enqueueUniqueWork(workName(request.recording), ExistingWorkPolicy.APPEND_OR_REPLACE, work)
    }

    fun cancel(context: Context, recording: String) {
        WorkManager.getInstance(context).cancelUniqueWork(workName(recording))
    }

    fun workName(recording: String) = "download-$recording"

    fun recordingTag(recording: String) = "recording:$recording"

    internal fun started(recording: String, downloader: ChunkDownloader) {
        running[recording] = downloader
    }

    internal fun report(recording: String, progress: DownloadProgress) = _progress.update { it + (recording to progress) }

    internal fun finished(recording: String, downloader: ChunkDownloader) {
        running.remove(recording, downloader)
        _progress.update { it - recording }
    }

    fun noticeDismissed(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_NOTICE_DISMISSED, false)

    fun showMeteredNotice() {
        _meteredNotice.value = true
    }

    fun closeMeteredNotice(context: Context, forever: Boolean) {
        _meteredNotice.value = false
        if (forever) context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_NOTICE_DISMISSED, true).apply()
    }
}
