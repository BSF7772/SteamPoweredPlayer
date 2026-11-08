// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.time.Instant
import kotlin.math.abs
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

enum class TimelineEntryType { Event, StateDescription, GameMode, Screenshot, Achievement, Phase, Error, Unknown }

data class TimelineTag(val name: String, val group: String?, val icon: String?, val priority: Long?)

/** One entry of a timeline file. [timeMs] and [durationMs] are on the timeline's own axis. */
data class TimelineEntry(
    val id: String?,
    val timeMs: Long,
    val type: TimelineEntryType,
    val rawType: String,
    val title: String?,
    val description: String?,
    val icon: String?,
    val priority: Long?,
    val durationMs: Long,
    val possibleClip: Boolean,
    val mode: String?,
    val tags: List<TimelineTag>,
    val achievementName: String?,
    val handle: String?,
)

/** A parsed `timelines/timeline_*.json`. Bad entries are skipped and listed in [diagnostics]. */
data class TimelineFile(
    val dateRecordedUtc: Instant?,
    val startTimeMs: Long?,
    val endTimeMs: Long?,
    val entries: List<TimelineEntry>,
    val diagnostics: List<String>,
)

/** Lenient reader for Steam's timeline JSON, as in the desktop's `Timeline`: numbers may be strings, unknown types are kept, bad content never throws. Pure. */
object Timeline {
    @OptIn(ExperimentalSerializationApi::class)
    private val json = Json { allowTrailingComma = true; allowComments = true }
    private val decimal = Regex("""^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$""")
    private const val MAX_UNIX_SECONDS = 253402300799L
    private const val MAX_MAGNITUDE = 9e18

    fun parse(utf8: ByteArray): TimelineFile {
        val body = if (utf8.size >= 3 && utf8[0] == 0xEF.toByte() && utf8[1] == 0xBB.toByte() && utf8[2] == 0xBF.toByte()) utf8.copyOfRange(3, utf8.size) else utf8
        val d = ArrayList<String>()
        val root = try {
            json.parseToJsonElement(body.toString(Charsets.UTF_8))
        } catch (e: SerializationException) {
            return TimelineFile(null, null, null, emptyList(), listOf("invalid JSON: ${e.message}"))
        }
        if (root !is JsonObject) return TimelineFile(null, null, null, emptyList(), listOf("root is not an object"))

        var date: Instant? = null
        root["daterecorded"]?.let { value ->
            val seconds = int64Of(value)
            if (seconds != null && seconds > 0 && seconds < MAX_UNIX_SECONDS) date = Instant.ofEpochSecond(seconds) else d.add("daterecorded: not a unix time")
        }
        val start = get(root, "starttime", d)
        val end = get(root, "endtime", d)

        val entries = ArrayList<TimelineEntry>()
        when (val array = root["entries"]) {
            is JsonArray -> array.forEachIndexed { i, e -> readEntry(e, i, d)?.let(entries::add) }
            null, JsonNull -> Unit
            // Steam writes "entries": {} for a timeline without events: an empty list, not an error.
            is JsonObject -> if (array.isNotEmpty()) d.add("entries: not an array")
            else -> d.add("entries: not an array")
        }
        return TimelineFile(date, start, end, entries, d)
    }

    private fun readEntry(e: JsonElement, index: Int, d: MutableList<String>): TimelineEntry? {
        val at = "entries[$index]"
        if (e !is JsonObject) {
            d.add("$at: not an object")
            return null
        }
        val time = get(e, "time", null)
        if (time == null) {
            d.add("$at: no usable time, skipped")
            return null
        }
        val rawType = str(e, "type") ?: ""
        val type = when (rawType) {
            "event" -> TimelineEntryType.Event
            "state_description" -> TimelineEntryType.StateDescription
            "gamemode" -> TimelineEntryType.GameMode
            "screenshot" -> TimelineEntryType.Screenshot
            "achievement" -> TimelineEntryType.Achievement
            "phase" -> TimelineEntryType.Phase
            "error" -> TimelineEntryType.Error
            else -> TimelineEntryType.Unknown
        }
        val tags = ArrayList<TimelineTag>()
        (e["tags"] as? JsonArray)?.forEach { t ->
            val name = (t as? JsonObject)?.let { str(it, "name") }
            if (name != null) tags.add(TimelineTag(name, str(t as JsonObject, "group"), str(t, "icon"), get(t, "priority", null))) else d.add("$at.tags: bad tag skipped")
        }
        val duration = (get(e, "duration", d) ?: 0).coerceAtLeast(0)
        return TimelineEntry(
            str(e, "id"), time, type, rawType, str(e, "title"), str(e, "description"), str(e, "icon"), get(e, "priority", d), duration,
            boolOf(e, "possible_clip"), str(e, "mode"), tags, str(e, "achievement_name"), str(e, "handle"),
        )
    }

    /** A string, or the plain text of a number or boolean; null for anything else. */
    private fun str(o: JsonObject, name: String): String? = (o[name] as? JsonPrimitive)?.takeUnless { it is JsonNull }?.content

    private fun get(o: JsonObject, name: String, d: MutableList<String>?): Long? {
        val value = o[name]
        if (value == null || value is JsonNull) return null
        return int64Of(value).also { if (it == null) d?.add("$name: not a number") }
    }

    private fun int64Of(value: JsonElement): Long? {
        val p = value as? JsonPrimitive ?: return null
        if (p is JsonNull || (!p.isString && (p.content == "true" || p.content == "false"))) return null
        val text = if (p.isString) p.content.trim() else p.content
        text.toLongOrNull()?.let { return it }
        if (!decimal.matches(text)) return null
        val number = text.toDoubleOrNull() ?: return null
        return if (number.isFinite() && abs(number) < MAX_MAGNITUDE) Math.rint(number).toLong() else null     // rint: halves to even, as on the desktop
    }

    private fun boolOf(o: JsonObject, name: String): Boolean {
        val p = o[name] as? JsonPrimitive ?: return false
        if (p is JsonNull) return false
        if (p.isString) return p.content.equals("true", ignoreCase = true) || p.content == "1"
        if (p.content == "true") return true
        if (p.content == "false") return false
        return p.content.toDoubleOrNull()?.let { it != 0.0 } ?: false
    }
}
