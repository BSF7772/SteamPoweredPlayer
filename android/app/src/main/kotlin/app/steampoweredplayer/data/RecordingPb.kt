// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

/** One protobuf field. [payload] is the length-delimited body or the 4/8 bytes of a fixed field; [varint] is a varint's value. */
class PbField(val number: Int, val wireType: Int, val varint: ULong, val payload: ByteArray) {
    /** A number from a varint, a fixed field, or a length-delimited decimal string (Steam ids are sometimes strings). */
    fun asUInt64(): ULong? = when (wireType) {
        VARINT -> varint
        FIXED64, FIXED32 -> payload.foldIndexed(0UL) { i, sum, b -> sum or (b.toUByte().toULong() shl (8 * i)) }
        LENGTH_DELIMITED -> text().trim().takeIf { s -> s.isNotEmpty() && s.all { it in '0'..'9' } }?.toULongOrNull()
        else -> null
    }

    fun asInt64(): Long? =
        if (wireType == LENGTH_DELIMITED) text().trim().toLongOrNull() else asUInt64()?.toLong()

    /** The text of a length-delimited field, or the decimal text of a numeric one (a timeline id may be either). */
    fun asText(): String? = if (wireType == LENGTH_DELIMITED) text() else asUInt64()?.toString()

    private fun text() = payload.toString(Charsets.UTF_8)

    companion object {
        const val VARINT = 0
        const val FIXED64 = 1
        const val LENGTH_DELIMITED = 2
        const val FIXED32 = 5
    }
}

/** A schema-less message: the fields read before any [error]. The phone never writes these files, so unlike the desktop's it keeps no raw bytes. */
class PbMessage private constructor(val fields: List<PbField>, val error: String?) {
    fun first(number: Int): PbField? = fields.firstOrNull { it.number == number }
    fun all(number: Int): List<PbField> = fields.filter { it.number == number }

    companion object {
        /** Never throws on garbage: an [error] is recorded and the rest is ignored. */
        fun parse(data: ByteArray): PbMessage {
            val fields = ArrayList<PbField>()
            var pos = 0

            fun varint(): ULong? {
                var value = 0UL
                var shift = 0
                var i = pos
                while (i < data.size && shift < 70) {
                    val b = data[i].toInt() and 0xFF
                    if (shift == 63 && b > 1) return null
                    value = value or ((b and 0x7F).toULong() shl shift)
                    if (b and 0x80 == 0) {
                        pos = i + 1
                        return value
                    }
                    i++
                    shift += 7
                }
                return null
            }

            while (pos < data.size) {
                val start = pos
                val problem: String? = run {
                    val tag = varint() ?: return@run "truncated or overlong tag"
                    val number = tag shr 3
                    val wire = (tag and 7UL).toInt()
                    if (number == 0UL || number > 0x1FFFFFFFUL) return@run "invalid field number"
                    when (wire) {
                        PbField.VARINT -> {
                            val v = varint() ?: return@run "truncated varint"
                            fields.add(PbField(number.toInt(), wire, v, EMPTY))
                        }
                        PbField.FIXED64, PbField.FIXED32 -> {
                            val n = if (wire == PbField.FIXED64) 8 else 4
                            if (data.size - pos < n) return@run "truncated fixed field"
                            fields.add(PbField(number.toInt(), wire, 0UL, data.copyOfRange(pos, pos + n)))
                            pos += n
                        }
                        PbField.LENGTH_DELIMITED -> {
                            val length = varint()
                            if (length == null || length > (data.size - pos).toULong()) return@run "truncated length-delimited field"
                            val n = length.toInt()
                            fields.add(PbField(number.toInt(), wire, 0UL, data.copyOfRange(pos, pos + n)))
                            pos += n
                        }
                        else -> return@run "unsupported wire type $wire"
                    }
                    null
                }
                if (problem != null) return PbMessage(fields, "$problem at offset $start")
            }
            return PbMessage(fields, null)
        }

        private val EMPTY = ByteArray(0)
    }
}

/** One recording of a timeline: [folder] is `recording_id` (1), the media folder's name; [offsetMs] is `recording_zero_timeline_offset_ms` (10), the exact offset. */
data class SessionRef(val folder: String?, val startMs: Long?, val durationMs: Long?, val offsetMs: Long?, val recordingType: Long?, val fileSize: Long?)

data class TimelineRef(val timelineId: String?, val gameId: ULong?, val dateRecordedUnix: Long?, val durationMs: Long?, val recordings: List<SessionRef>) {
    val session: SessionRef? get() = recordings.firstOrNull()
}

data class PbTag(val name: String?, val group: String?, val icon: String?, val priority: Long?)
data class PbTagOccurrence(val timelineId: String?, val offsetMs: Long?)
data class PbTagIndexEntry(val appId: ULong?, val tag: PbTag?, val occurrences: List<PbTagOccurrence>)

/** A parsed `gamerecording.pb`. */
data class GameRecordingPb(val timelines: List<TimelineRef>, val tagIndex: List<PbTagIndexEntry>, val diagnostics: List<String>) {
    /** The exact offset (field 1.5.10, ms) of the first recording of [timelineId], or null. */
    fun offsetMsFor(timelineId: String): Long? = timelines.firstOrNull { it.timelineId == timelineId }?.session?.offsetMs

    /** The exact offset for the recording in media folder [folder], searching every recording of every timeline. */
    fun offsetMsForFolder(folder: String): Long? = recordingNamed(timelines, folder)?.offsetMs
}

/** A parsed `clip.pb`. */
data class ClipPb(
    val timelines: List<TimelineRef>,
    val dateRecordedUnix: Long?,
    val gameId: ULong?,
    val title: String?,
    val thumbnailWidth: Int?,
    val thumbnailHeight: Int?,
    val firstTimelineStartOffsetMs: Long?,
    val temporary: Boolean?,
    val diagnostics: List<String>,
) {
    val timeline: TimelineRef? get() = timelines.firstOrNull()

    /** The exact offset of the clip's first recording inside its timeline. */
    val offsetMs: Long? get() = timeline?.session?.offsetMs

    /** The folders under `clip_*\/video/`, in order. */
    val mediaFolders: List<String> get() = timelines.flatMap { it.recordings }.mapNotNull { it.folder }

    fun offsetMsForFolder(folder: String): Long? = recordingNamed(timelines, folder)?.offsetMs
}

private fun recordingNamed(timelines: List<TimelineRef>, folder: String) =
    timelines.flatMap { it.recordings }.firstOrNull { it.folder == folder }

/**
 * Reader for Steam's `gamerecording.pb` and `clip.pb`, as in the desktop's `RecordingPb`. Hand-written on the wire format: unknown fields are ignored, odd
 * wire types tolerated, nothing throws on bad input; problems go in the diagnostics. Pure.
 */
object RecordingPb {
    fun parseGameRecording(data: ByteArray): GameRecordingPb {
        val d = ArrayList<String>()
        val raw = PbMessage.parse(data)
        raw.error?.let(d::add)
        val timelines = raw.all(1).mapNotNull { f -> subMessage(f, "1", d)?.let { readTimeline(it, "1", d) } }
        val tags = raw.all(4).mapNotNull { f -> subMessage(f, "4", d)?.let { readTagEntry(it, d) } }
        return GameRecordingPb(timelines, tags, d)
    }

    fun parseClip(data: ByteArray): ClipPb {
        val d = ArrayList<String>()
        val raw = PbMessage.parse(data)
        raw.error?.let(d::add)
        val timelines = raw.all(1).mapNotNull { f -> subMessage(f, "1", d)?.let { readTimeline(it, "1", d) } }
        return ClipPb(
            timelines = timelines,
            dateRecordedUnix = int64(raw, 3, "", d),
            gameId = uint64(raw, 4, "", d),
            title = text(raw, 7, "", d),
            thumbnailWidth = toInt(int64(raw, 12, "", d)),
            thumbnailHeight = toInt(int64(raw, 13, "", d)),
            firstTimelineStartOffsetMs = raw.first(2)?.asInt64(),
            temporary = raw.first(8)?.asInt64()?.let { it != 0L },
            diagnostics = d,
        )
    }

    private fun readTimeline(m: PbMessage, path: String, d: MutableList<String>): TimelineRef {
        val p = "$path.5"
        val recordings = m.all(5).mapNotNull { f ->
            subMessage(f, p, d)?.let { s ->
                SessionRef(text(s, 1, p, d), int64(s, 2, p, d), int64(s, 3, p, d), int64(s, 10, p, d), s.first(4)?.asInt64(), s.first(9)?.asInt64())
            }
        }
        return TimelineRef(text(m, 1, path, d), uint64(m, 2, path, d), int64(m, 3, path, d), int64(m, 4, path, d), recordings)
    }

    private fun readTagEntry(m: PbMessage, d: MutableList<String>): PbTagIndexEntry {
        val tag = m.first(2)?.let { subMessage(it, "4.2", d) }?.let { t ->
            PbTag(text(t, 1, "4.2", d), text(t, 2, "4.2", d), text(t, 3, "4.2", d), int64(t, 4, "4.2", d))
        }
        val occurrences = m.all(3).mapNotNull { f -> subMessage(f, "4.3", d)?.let { PbTagOccurrence(text(it, 2, "4.3", d), int64(it, 3, "4.3", d)) } }
        return PbTagIndexEntry(uint64(m, 1, "4", d), tag, occurrences)
    }

    private fun subMessage(f: PbField, path: String, d: MutableList<String>): PbMessage? {
        if (f.wireType != PbField.LENGTH_DELIMITED) {
            d.add("$path: expected a message, got wire type ${f.wireType}")
            return null
        }
        val m = PbMessage.parse(f.payload)
        if (m.error != null) {
            d.add("$path: ${m.error}")
            return null
        }
        return m
    }

    private fun fieldPath(path: String, n: Int) = if (path.isEmpty()) "$n" else "$path.$n"

    private fun uint64(m: PbMessage, n: Int, path: String, d: MutableList<String>): ULong? {
        val f = m.first(n) ?: return null
        return f.asUInt64().also { if (it == null) d.add("${fieldPath(path, n)}: not a number (wire type ${f.wireType})") }
    }

    private fun int64(m: PbMessage, n: Int, path: String, d: MutableList<String>): Long? {
        val f = m.first(n) ?: return null
        return f.asInt64().also { if (it == null) d.add("${fieldPath(path, n)}: not a number (wire type ${f.wireType})") }
    }

    private fun text(m: PbMessage, n: Int, path: String, d: MutableList<String>): String? {
        val f = m.first(n) ?: return null
        return f.asText().also { if (it == null) d.add("${fieldPath(path, n)}: not text (wire type ${f.wireType})") }
    }

    private fun toInt(v: Long?): Int? = v?.takeIf { it in Int.MIN_VALUE..Int.MAX_VALUE }?.toInt()
}
