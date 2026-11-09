// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkManager
import androidx.work.workDataOf
import java.io.File
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

@Serializable
data class SpanRequest(val start: Double, val end: Double)

@Serializable
data class ChapterRequest(val seconds: Double, val title: String)

/** One export, kept in the work queue. Edits apply to the phone's own file: nothing is written to the share. */
@Serializable
data class ExportRequest(
    val id: String = UUID.randomUUID().toString(),
    val profileId: String,
    val libraryKey: String,
    val recording: String,
    val title: String,
    val stem: String,
    val spans: List<SpanRequest>,
    /** A ladder tier's id, a special preset's, or [ExportLadder.SAVE_CLIP_ID]. */
    val choice: String,
    val engine: LadderEngine = LadderEngine.Auto,
    val targetMegabytes: Double? = null,
    val speed: Double = 1.0,
    val volumePercent: Double = 100.0,
    val muted: Boolean = false,
    val audioDelay: Double = 0.0,
    val chapters: List<ChapterRequest> = emptyList(),
    /** Open the share sheet when it's done. It always uses the tier the user chose. */
    val share: Boolean = false,
) {
    fun encode(): String = json.encodeToString(serializer(), this)

    companion object {
        private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
        fun decode(text: String): ExportRequest = json.decodeFromString(serializer(), text)
    }
}

data class ExportActivity(val request: ExportRequest, val stage: String, val fraction: Double)

/** A finished export, as the Exports screen lists it. */
@Serializable
data class ExportRecord(
    val id: String,
    val title: String,
    val displayName: String,
    val uri: String?,
    val bytes: Long,
    val choice: String,
    val whenEpochSeconds: Long,
    val error: String? = null,
)

/** The export queue, its progress, and what came of it. */
object Exports {
    private const val QUEUE = "exports"
    const val TAG_ALL = "export"
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true; prettyPrint = true }
    private val _active = MutableStateFlow<ExportActivity?>(null)
    val active: StateFlow<ExportActivity?> = _active
    private val _queued = MutableStateFlow<List<ExportRequest>>(emptyList())
    val queued: StateFlow<List<ExportRequest>> = _queued
    private val _finished = MutableSharedFlow<ExportRecord>(extraBufferCapacity = 8, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    private val _shareRequests = MutableSharedFlow<ExportRecord>(extraBufferCapacity = 8, onBufferOverflow = BufferOverflow.DROP_OLDEST)

    /** Each export as it ends, also a failed one. */
    val finished: SharedFlow<ExportRecord> = _finished

    /** Exports that asked for the share sheet when done. */
    val shareRequests: SharedFlow<ExportRecord> = _shareRequests

    fun enqueue(context: Context, request: ExportRequest) {
        val work = OneTimeWorkRequestBuilder<ExportWorker>()
            .setConstraints(Constraints.NONE)
            .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
            .setInputData(workDataOf(ExportWorker.KEY_REQUEST to request.encode()))
            .addTag(TAG_ALL)
            .build()
        _queued.update { it + request }
        WorkManager.getInstance(context).enqueueUniqueWork(QUEUE, ExistingWorkPolicy.APPEND_OR_REPLACE, work)
    }

    fun cancelAll(context: Context) {
        _queued.value = emptyList()
        WorkManager.getInstance(context).cancelUniqueWork(QUEUE)
    }

    internal fun progress(activity: ExportActivity?) {
        _active.value = activity
        if (activity != null) _queued.update { q -> q.filterNot { it.id == activity.request.id } }
    }

    internal fun done(context: Context, record: ExportRecord, share: Boolean) {
        add(context, record)
        _finished.tryEmit(record)
        if (share && record.uri != null) _shareRequests.tryEmit(record)
    }

    private fun file(context: Context) = File(context.applicationContext.filesDir, "exports.json")

    @Synchronized
    fun history(context: Context): List<ExportRecord> = try {
        file(context).takeIf { it.exists() }?.let { json.decodeFromString<List<ExportRecord>>(it.readText()) }.orEmpty()
    } catch (_: SerializationException) {
        emptyList()
    } catch (_: IOException) {
        emptyList()
    }

    @Synchronized
    private fun add(context: Context, record: ExportRecord) = writeHistory(context, listOf(record) + history(context))

    @Synchronized
    fun forget(context: Context, id: String) = writeHistory(context, history(context).filterNot { it.id == id })

    private fun writeHistory(context: Context, list: List<ExportRecord>) {
        val f = file(context)
        val tmp = File(f.path + ".tmp")
        tmp.writeText(json.encodeToString(list))
        if (!tmp.renameTo(f)) {
            f.delete()
            tmp.renameTo(f)
        }
    }

    /** Copies a finished file to the shared Movies folder. The system adds a number when the name is taken. */
    fun publish(context: Context, file: File, displayName: String): Uri {
        val resolver = context.contentResolver
        val values = ContentValues().apply {
            put(MediaStore.Video.Media.DISPLAY_NAME, displayName)
            put(MediaStore.Video.Media.MIME_TYPE, "video/mp4")
            put(MediaStore.Video.Media.RELATIVE_PATH, Environment.DIRECTORY_MOVIES + "/SteamPoweredPlayer")
            put(MediaStore.Video.Media.IS_PENDING, 1)
        }
        val uri = resolver.insert(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, values) ?: throw IOException("The phone's media library refused the file.")
        try {
            resolver.openOutputStream(uri)?.use { out -> file.inputStream().use { it.copyTo(out, 1 shl 20) } } ?: throw IOException("Can't write to the media library.")
            resolver.update(uri, ContentValues().apply { put(MediaStore.Video.Media.IS_PENDING, 0) }, null, null)
        } catch (e: IOException) {
            resolver.delete(uri, null, null)
            throw e
        }
        return uri
    }
}
