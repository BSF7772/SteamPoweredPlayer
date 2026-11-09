// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.edit

import app.steampoweredplayer.data.isPlainName
import java.io.File
import java.io.IOException
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

/** What a recording's edit is on this phone. Nothing here goes to the share. */
@Serializable
data class RecordingEdit(
    val kept: List<KeptRange> = emptyList(),
    val markIn: Double? = null,
    val markOut: Double? = null,
    val audioDelay: Double = 0.0,
    val speed: Double = 1.0,
    val volumePercent: Double = 100.0,
    val muted: Boolean = false,
    val resumeSeconds: Double? = null,
)

@Serializable
data class KeptRange(val start: Double, val end: Double)

/** One JSON file per recording. A write replaces the file whole. */
class EditStore(private val dir: File) {
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    fun load(key: String): RecordingEdit {
        require(isPlainName(key))
        return try {
            File(dir, "$key.json").takeIf { it.exists() }?.let { json.decodeFromString(RecordingEdit.serializer(), it.readText()) } ?: RecordingEdit()
        } catch (_: SerializationException) {
            RecordingEdit()
        } catch (_: IOException) {
            RecordingEdit()
        }
    }

    fun save(key: String, edit: RecordingEdit) {
        require(isPlainName(key))
        dir.mkdirs()
        val tmp = File(dir, "$key.json.tmp")
        tmp.writeText(json.encodeToString(RecordingEdit.serializer(), edit))
        val file = File(dir, "$key.json")
        if (!tmp.renameTo(file)) {
            file.delete()
            tmp.renameTo(file)
        }
    }
}
