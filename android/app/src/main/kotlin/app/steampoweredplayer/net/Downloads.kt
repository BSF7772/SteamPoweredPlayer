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
import app.steampoweredplayer.data.LocalChunkCache
import java.io.File
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** The app's download queue and the state the screens read. */
object Downloads {
    private const val PREFS = "downloads"
    private const val KEY_NOTICE_DISMISSED = "metered_notice_dismissed"
    private const val CACHE_BYTES = 20L * 1024 * 1024 * 1024

    private val _meteredNotice = MutableStateFlow(false)
    val meteredNotice: StateFlow<Boolean> = _meteredNotice

    @Volatile private var cache: LocalChunkCache? = null

    /** The one chunk cache, opened on first use. Blocking: call off the main thread. */
    fun cache(context: Context): LocalChunkCache = cache ?: synchronized(this) {
        cache ?: LocalChunkCache.open(File(context.applicationContext.noBackupFilesDir, "chunks"), CACHE_BYTES).also { cache = it }
    }

    /** Starts [request], or leaves it alone when it's already queued or running. Survives the app closing and a reboot. */
    fun enqueue(context: Context, request: DownloadRequest) {
        val work = OneTimeWorkRequestBuilder<DownloadWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
            .setInputData(workDataOf(DownloadWorker.KEY_REQUEST to request.encode()))
            .build()
        WorkManager.getInstance(context).enqueueUniqueWork(workName(request.recording), ExistingWorkPolicy.KEEP, work)
    }

    fun cancel(context: Context, recording: String) {
        WorkManager.getInstance(context).cancelUniqueWork(workName(recording))
    }

    fun workName(recording: String) = "download-$recording"

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
