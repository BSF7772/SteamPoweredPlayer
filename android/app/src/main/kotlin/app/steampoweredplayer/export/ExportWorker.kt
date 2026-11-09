// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkInfo
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.MainActivity
import app.steampoweredplayer.R
import app.steampoweredplayer.data.ChunkKey
import app.steampoweredplayer.data.ProfileStore
import app.steampoweredplayer.data.RecordingRepository
import app.steampoweredplayer.data.SessionMedia
import app.steampoweredplayer.net.DownloadRequest
import app.steampoweredplayer.net.Downloads
import java.io.File
import java.io.IOException
import java.time.Instant
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.merge
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext

/** Runs one [ExportRequest] as a foreground job with a progress notification: fetches what's missing, makes the file, puts it in the shared Movies folder. */
class ExportWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    private val app = context.applicationContext
    private var foregroundRefused = false
    private var lastPercent = -1

    override suspend fun getForegroundInfo(): ForegroundInfo = foreground("", 0)

    override suspend fun doWork(): Result {
        val request = inputData.getString(KEY_REQUEST)?.let(ExportRequest::decode) ?: return Result.failure()
        val started = Instant.now().epochSecond
        val displayName = request.stem + ExportLadder.fileSuffix(request.choice) + ".mp4"
        return try {
            stage(request, "Starting", 0.0)
            val work = File(app.cacheDir, "export-${request.id}").apply { mkdirs() }
            try {
                val output = File(work, "out.mp4")
                val media = prepare(request)
                make(request, media, work, output)
                stage(request, "Saving", 0.99)
                val uri = withContext(Dispatchers.IO) { Exports.publish(app, output, displayName) }
                Exports.done(app, ExportRecord(request.id, request.title, displayName, uri.toString(), output.length(), request.choice, started), request.share)
                Log.i(TAG, "done: ${request.choice}, ${output.length()} bytes")
            } finally {
                work.deleteRecursively()
            }
            Result.success()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w(TAG, "failed: ${e.javaClass.simpleName}")
            Exports.done(app, ExportRecord(request.id, request.title, displayName, null, 0, request.choice, started, e.message ?: e.javaClass.simpleName), false)
            Result.failure()
        } finally {
            Exports.progress(null)
        }
    }

    /** The recording's media on the phone. Chunks that aren't here yet are fetched first, as an ordinary download. */
    private suspend fun prepare(request: ExportRequest): SessionMedia {
        val cache = withContext(Dispatchers.IO) { Downloads.cache(app) }
        val details = withContext(Dispatchers.IO) { RecordingRepository(File(app.filesDir, "library/${request.profileId}/recordings"), null, null).stored(request.recording) }
            ?: throw IOException("This recording's details aren't on this phone. Open it once while connected.")
        val media = details.media(cache.folderOf(request.libraryKey, request.recording).path)
        val plans = ExportSpans.plans(media, request.spans.map { ExportSpan(it.start, it.end) })
        if (plans.isEmpty()) throw IOException("Nothing to export: this range has no chunks.")
        val (video, audio) = LosslessExporter.chunksNeeded(media, plans, request.audioDelay)
        val needed = (video + audio).distinct().sorted()
        val required = (0..1).map { ChunkKey(request.libraryKey, request.recording, it, 0) } +
            video.map { ChunkKey(request.libraryKey, request.recording, 0, it) } + audio.map { ChunkKey(request.libraryKey, request.recording, 1, it) }
        fun missing() = required.filterNot { it in cache }
        if (missing().isEmpty()) return media

        val profiles = withContext(Dispatchers.IO) { ProfileStore(File(app.filesDir, "servers.json")).load() }
        if (profiles.none { it.id == request.profileId }) throw IOException("The server this recording came from is gone.")
        Downloads.fetch(app, DownloadRequest(request.profileId, request.recording, details.mediaFolder, needed, needed.first()))
        val total = required.size
        val manager = WorkManager.getInstance(app)
        while (missing().isNotEmpty()) {
            stage(request, "Downloading", 0.2 * (total - missing().size) / total)
            currentCoroutineContext().ensureActive()
            // Wakes on a finished file, or when the download job itself ends: then a file that's still missing won't come.
            val infos = manager.getWorkInfosForUniqueWorkFlow(Downloads.workName(request.recording))
            val ended = merge(cache.completed.asUnit(), infos.asFinished()).first()
            if (ended == Wake.JobEnded && missing().isNotEmpty()) throw IOException("Part of this range couldn't be downloaded: Steam may have removed it.")
        }
        return media
    }

    private enum class Wake { File, JobEnded }

    private fun kotlinx.coroutines.flow.SharedFlow<ChunkKey>.asUnit() = kotlinx.coroutines.flow.flow { collect { emit(Wake.File) } }

    private fun kotlinx.coroutines.flow.Flow<List<WorkInfo>>.asFinished() = kotlinx.coroutines.flow.flow {
        var runningSeen = false
        collect { list ->
            val running = list.any { !it.state.isFinished }
            if (running) runningSeen = true
            if (!running && (runningSeen || list.isNotEmpty())) emit(Wake.JobEnded)
        }
    }

    private suspend fun make(request: ExportRequest, media: SessionMedia, work: File, output: File) {
        val spans = request.spans.map { ExportSpan(it.start, it.end) }
        if (request.choice == ExportLadder.SAVE_CLIP_ID) {
            val coroutine = currentCoroutineContext()
            withContext(Dispatchers.IO) {
                LosslessExporter.export(
                    media, spans, request.audioDelay, request.chapters.map { ExportChapter(it.seconds, it.title) }, output,
                    cancelled = { !coroutine.isActive || isStopped },
                    progress = { f -> stageBlocking(request, "Saving clip", 0.2 + f * 0.79) },
                )
            }
            return
        }
        val tier = ExportLadder.tier(request.choice) ?: throw IOException("Unknown export choice.")
        val seconds = ExportSpans.outputSeconds(ExportSpans.plans(media, spans), request.speed)
        val (width, height, fps) = withContext(Dispatchers.IO) { SourceFormat.read(media) }
        val plan = ExportLadder.plan(tier, request.engine, DeviceEncoders.list(), width, height, fps, seconds, request.targetMegabytes)
            ?: throw IOException("This phone has no encoder for ${tier.name} at ${width}x$height on the chosen engine.")
        val edits = ReEncodeEdits(request.speed, request.volumePercent, request.muted, request.audioDelay)
        val encoder = ReEncoder(app, AppSettings(app).fastReencode)
        withContext(Dispatchers.IO) {
            encoder.run(media, spans, plan, edits, File(work, "parts"), output) { f -> stageBlocking(request, "Encoding", 0.2 + f * 0.79) }
            val megabytes = request.targetMegabytes
            if (tier.id == ExportLadder.TARGET_ID && megabytes != null) {
                ExportLadder.retryForSize(plan, (megabytes * 1024 * 1024).toLong(), output.length())?.let { again ->
                    Log.i(TAG, "over the size budget: encoding again at ${again.videoBitsPerSecond} b/s")
                    output.delete()
                    encoder.run(media, spans, again, edits, File(work, "parts"), output) { f -> stageBlocking(request, "Fitting the size", 0.2 + f * 0.79) }
                }
            }
        }
    }

    private fun stageBlocking(request: ExportRequest, stage: String, fraction: Double) {
        Exports.progress(ExportActivity(request, stage, fraction))
        val percent = (fraction * 100).toInt()
        if (percent == lastPercent) return
        lastPercent = percent
        app.getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification(request.title, percent))
    }

    private suspend fun stage(request: ExportRequest, stage: String, fraction: Double) {
        Exports.progress(ExportActivity(request, stage, fraction))
        val percent = (fraction * 100).toInt()
        if (percent == lastPercent || foregroundRefused) return
        lastPercent = percent
        try {
            setForeground(foreground(request.title, percent))
        } catch (e: IllegalStateException) {
            foregroundRefused = true
            Log.w(TAG, "no foreground: ${e.javaClass.simpleName}")
        }
    }

    private fun foreground(title: String, percent: Int) = ForegroundInfo(NOTIFICATION_ID, notification(title, percent), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)

    private fun notification(title: String, percent: Int): Notification {
        val manager = app.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, app.getString(R.string.export_channel), NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(app, 0, Intent(app, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(app, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentTitle(app.getString(R.string.export_title))
            .setContentText(title)
            .setContentIntent(open)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setProgress(100, percent, false)
            .build()
    }

    companion object {
        const val KEY_REQUEST = "request"
        private const val TAG = "SppExport"
        private const val CHANNEL = "exports"
        private const val NOTIFICATION_ID = 42
    }
}
