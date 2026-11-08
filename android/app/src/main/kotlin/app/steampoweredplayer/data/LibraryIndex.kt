// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.time.Instant
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeParseException
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.intOrNull

/** Art paths relative to the data folder, forward slashes. Null = not available. */
@Serializable
data class GameArt(val cover: String? = null, val hero: String? = null, val heroBlur: String? = null, val logo: String? = null)

/** [gameId] is a decimal string: shortcut ids exceed 2^53. */
@Serializable
data class GameEntry(val gameId: String, val name: String, val isShortcut: Boolean = false, val art: GameArt? = null)

@Serializable
enum class WireKind(val kind: RecordingKind) {
    @SerialName("cache") Cache(RecordingKind.CacheSession),
    @SerialName("clip") Clip(RecordingKind.Clip),
    @SerialName("downloadedClip") DownloadedClip(RecordingKind.DownloadedClip),
}

@Serializable
data class RecordingEntry(
    @SerialName("kind") val wireKind: WireKind,
    val gameId: String? = null,
    @Serializable(UtcInstantSerializer::class) val startUtc: Instant,
    val relativeFolder: String,
    val runs: List<@Serializable(SegmentRunSerializer::class) SegmentRun>,
    val segmentSeconds: Double = SessionMedia.DEFAULT_SEGMENT_SECONDS,
    val durationSeconds: Double = 0.0,
    val live: Boolean = false,
    val temporary: Boolean = false,
    val title: String? = null,
    val markerCount: Int = 0,
    val exactOffsetMs: Long? = null,
    val sizeBytes: Long = 0,
) {
    val kind: RecordingKind get() = wireKind.kind
}

/** `library.json` v1. Only the desktop writes it (docs/data-folder.md); the phone reads, and never writes to the share. */
@Serializable
data class LibraryIndex(
    val schemaVersion: Int,
    val libraryId: String,
    @Serializable(UtcInstantSerializer::class) val generatedUtc: Instant,
    val games: List<GameEntry>,
    val recordings: List<RecordingEntry>,
) {
    companion object {
        const val CURRENT_SCHEMA_VERSION = 1

        @OptIn(ExperimentalSerializationApi::class)
        private val json = Json {
            ignoreUnknownKeys = true
            allowTrailingComma = true
            allowComments = true
        }

        /** Never throws. A newer writer's file is read best effort, so [LibraryIndexParse.index] may be null: show "update the app". */
        fun parse(utf8: ByteArray): LibraryIndexParse {
            val text = utf8.toString(Charsets.UTF_8)
            val version = try {
                (json.parseToJsonElement(text) as? JsonObject)?.get("schemaVersion")?.let { (it as? JsonPrimitive)?.intOrNull } ?: 0
            } catch (e: SerializationException) {
                return LibraryIndexParse(LibraryIndexStatus.Corrupt, null, 0, e.message)
            }
            if (version < 1) return LibraryIndexParse(LibraryIndexStatus.Corrupt, null, version, "schemaVersion missing or < 1")
            val newer = version > CURRENT_SCHEMA_VERSION
            return try {
                val index = json.decodeFromString(serializer(), text)
                LibraryIndexParse(if (newer) LibraryIndexStatus.NewerSchema else LibraryIndexStatus.Ok, index, version, null)
            } catch (e: IllegalArgumentException) {       // SerializationException is one
                LibraryIndexParse(if (newer) LibraryIndexStatus.NewerSchema else LibraryIndexStatus.Corrupt, null, version, e.message)
            }
        }
    }
}

enum class LibraryIndexStatus { Ok, NewerSchema, Corrupt, Missing }

data class LibraryIndexParse(val status: LibraryIndexStatus, val index: LibraryIndex?, val schemaVersion: Int, val error: String?)

internal object UtcInstantSerializer : KSerializer<Instant> {
    override val descriptor = PrimitiveSerialDescriptor("UtcInstant", PrimitiveKind.STRING)

    override fun deserialize(decoder: Decoder): Instant {
        val s = decoder.decodeString()
        return try {
            OffsetDateTime.parse(s).toInstant()
        } catch (_: DateTimeParseException) {
            try {
                LocalDateTime.parse(s).toInstant(ZoneOffset.UTC)      // no offset: UTC, as on the desktop
            } catch (_: DateTimeParseException) {
                throw SerializationException("invalid UTC timestamp")
            }
        }
    }

    override fun serialize(encoder: Encoder, value: Instant) = encoder.encodeString(value.toString())
}

/** A run is `[first,last]`. */
internal object SegmentRunSerializer : KSerializer<SegmentRun> {
    private val pair = ListSerializer(Int.serializer())
    override val descriptor = pair.descriptor

    override fun deserialize(decoder: Decoder): SegmentRun {
        val v = pair.deserialize(decoder)
        if (v.size != 2) throw SerializationException("run must have exactly two numbers")
        if (v[0] < 0 || v[1] < v[0]) throw SerializationException("run bounds invalid")
        return SegmentRun(v[0], v[1])
    }

    override fun serialize(encoder: Encoder, value: SegmentRun) = pair.serialize(encoder, listOf(value.first, value.last))
}
