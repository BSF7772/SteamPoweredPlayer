// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.net

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.SystemClock
import android.text.format.Formatter
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import app.steampoweredplayer.R
import app.steampoweredplayer.data.ChunkDownloader
import app.steampoweredplayer.data.ChunkHeads
import app.steampoweredplayer.data.HeadCheck
import app.steampoweredplayer.data.readNBytesCompat
import app.steampoweredplayer.data.DownloadProgress
import app.steampoweredplayer.data.DownloadStatus
import app.steampoweredplayer.data.ProfileStore
import app.steampoweredplayer.data.ReconnectPolicy
import app.steampoweredplayer.data.RemoteFileGone
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.smb.PasswordVault
import app.steampoweredplayer.smb.SmbRemoteFiles
import java.io.File
import java.io.IOException
import kotlin.random.Random
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Runs one [DownloadRequest] as a foreground job: it outlives the app and comes back after a reboot, and a stopped job loses at most the bytes of
 * the files in flight, because each `.part` is resumed. Logs hold counts only: never a path, a share or a credential.
 */
class DownloadWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    private val app = context.applicationContext
    private var foregroundRefused = false

    override suspend fun getForegroundInfo(): ForegroundInfo = foreground(DownloadProgress(), "")

    override suspend fun doWork(): Result {
        val request = inputData.getString(KEY_REQUEST)?.let(DownloadRequest::decode) ?: return Result.failure()
        val profiles = withContext(Dispatchers.IO) { ProfileStore(File(app.filesDir, "servers.json")).load() }
        val route = DownloadRoutes.sources(request.profileId, profiles) ?: return Result.failure()
        val primary = route.first()
        val vault = PasswordVault(File(app.noBackupFilesDir, "vault"))
        val sources = withContext(Dispatchers.IO) {
            route.mapNotNull { profile -> vault.get(profile.id)?.let { SmbRemoteFiles(profile.atShareRoot(), it) } }
        }
        if (sources.isEmpty()) {
            Log.w(TAG, "no stored password")
            return Result.failure()
        }
        showForeground(DownloadProgress(), primary)

        val chunks = request.chunks ?: try {
            withContext(Dispatchers.IO) {
                val source = sources.first()
                DownloadRoutes.completeChunks(source.list(request.relativeFolder)) { name ->
                    try {
                        source.open("${request.relativeFolder}/$name", 0).use { ChunkHeads.classify(it.readNBytesCompat(ChunkHeads.HEAD_LENGTH)) }
                    } catch (_: RemoteFileGone) {
                        HeadCheck.Vanished
                    }
                }
            }
        } catch (_: RemoteFileGone) {
            Log.w(TAG, "recording is gone")
            return Result.failure()
        } catch (e: IOException) {
            Log.w(TAG, "listing failed, will retry: ${e.javaClass.simpleName}")
            return Result.retry()
        }

        val cache = withContext(Dispatchers.IO) { Downloads.cache(app) }
        val downloader = ChunkDownloader(
            cache, DownloadRoutes.libraryKey(primary), request.recording, request.relativeFolder, sources,
            ReconnectPolicy({ SystemClock.elapsedRealtime() }, Random.Default, sources.size), Dispatchers.IO,
        )
        val gate = MeteredNoticeGate { Downloads.noticeDismissed(app) }
        downloader.select(chunks, request.playheadChunk)
        Downloads.started(request.recording, downloader)
        try {
            LinkMonitor(app).use { link ->
                coroutineScope {
                    val helpers = listOf(
                        launch { link.changes.collect { downloader.onNetworkChanged() } },
                        launch { link.state.collect { if (gate.shouldShow(it)) Downloads.showMeteredNotice() } },
                        launch {
                            downloader.progress.collect {
                                Downloads.report(request.recording, it)
                                report(it, primary)
                                delay(REPORT_EVERY_MS)
                            }
                        },
                    )
                    downloader.run()
                    helpers.forEach { it.cancelAndJoin() }
                }
            }
        } finally {
            Downloads.finished(request.recording, downloader)
        }
        val end = downloader.progress.value
        report(end, primary)
        Log.i(TAG, "finished: ${end.filesDone}/${end.filesTotal} files, ${end.filesGone} gone, ${end.filesFailed} failed, ${end.bytesFromSource} bytes")
        return Result.success(outputOf(end))
    }

    private suspend fun report(progress: DownloadProgress, profile: ServerProfile) {
        Log.i(TAG, "${progress.status}: ${progress.filesDone}/${progress.filesTotal} files, ${progress.bytesFromSource} bytes")
        setProgress(outputOf(progress))
        if (progress.status != DownloadStatus.Done) showForeground(progress, profile)
    }

    private fun outputOf(p: DownloadProgress) = workDataOf(
        "status" to p.status.name, "filesTotal" to p.filesTotal, "filesDone" to p.filesDone, "filesGone" to p.filesGone,
        "filesFailed" to p.filesFailed, "bytesFromSource" to p.bytesFromSource,
    )

    /** The system may refuse a foreground start from the background (after a reboot, say); the job carries on without its notification. */
    private suspend fun showForeground(progress: DownloadProgress, profile: ServerProfile) {
        if (foregroundRefused) return
        try {
            setForeground(foreground(progress, profile.name))
        } catch (e: IllegalStateException) {
            foregroundRefused = true
            Log.w(TAG, "no foreground: ${e.javaClass.simpleName}")
        }
    }

    private fun foreground(progress: DownloadProgress, profileName: String): ForegroundInfo =
        ForegroundInfo(NOTIFICATION_ID, notification(progress, profileName), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)

    private fun notification(p: DownloadProgress, profileName: String): Notification {
        val manager = app.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, app.getString(R.string.download_channel), NotificationManager.IMPORTANCE_LOW))
        val text = when (p.status) {
            DownloadStatus.Reconnecting -> app.getString(R.string.download_reconnecting)
            DownloadStatus.CannotReach -> app.getString(R.string.download_cannot_reach, profileName)
            else -> app.getString(R.string.download_progress, p.filesDone, p.filesTotal, Formatter.formatShortFileSize(app, p.bytesFromSource))
        }
        return NotificationCompat.Builder(app, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(app.getString(R.string.download_title))
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setProgress(p.filesTotal, p.filesDone, p.filesTotal == 0)
            .build()
    }

    companion object {
        const val KEY_REQUEST = "request"
        private const val TAG = "SppDownload"
        private const val CHANNEL = "downloads"
        private const val NOTIFICATION_ID = 41
        private const val REPORT_EVERY_MS = 1000L
    }
}
