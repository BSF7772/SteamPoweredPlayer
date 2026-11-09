// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

enum class HeadCheck { Valid, ZeroFilled, TooShort, NotABox, Vanished, Unreadable }

/** The 8-byte chunk head check, as in the desktop's `ScanHeads`: a crash can leave a zero-filled file under a normal chunk name. Pure. */
object ChunkHeads {
    const val HEAD_LENGTH = 8

    /** Is [head] the start of an MP4 box? Size 0 (to end of file), 1 (64-bit size) or at least 8, with a printable type. */
    fun classify(head: ByteArray): HeadCheck {
        if (head.size < HEAD_LENGTH) return HeadCheck.TooShort
        if ((0 until HEAD_LENGTH).all { head[it] == 0.toByte() }) return HeadCheck.ZeroFilled
        val size = u32(head, 0)
        if (size != 0L && size != 1L && size < 8) return HeadCheck.NotABox
        for (i in 4 until 8) {
            val b = head[i].toInt().toChar()
            if (b !in 'a'..'z' && b !in 'A'..'Z' && b !in '0'..'9' && b != ' ') return HeadCheck.NotABox
        }
        return HeadCheck.Valid
    }
}

enum class FragmentStatus { Found, NeedMore, ZeroFilled, Invalid }

/** For [FragmentStatus.Found] the fragment is `bytes[offset, offset + length)` of the chunk. For [FragmentStatus.NeedMore], read up to [requiredBytes] and ask again. */
data class FirstFragmentInfo(val status: FragmentStatus, val offset: Long, val length: Long, val requiredBytes: Long, val error: String? = null)

/**
 * The start of a Steam chunk, as in the desktop's `FirstFragment` (spike S4): one sample per fragment, the first one is the IDR, and all sizes are in the
 * box headers, so a first read plus at most one exact second read covers it. Pure.
 */
object FirstFragment {
    const val DEFAULT_FIRST_READ = 256 * 1024
    const val MAX_FRAGMENT_BYTES = 32L * 1024 * 1024
    const val TFDT_HEAD_BYTES = 4096
    private const val MAX_SKIPPED_BOX = 1L shl 20

    private class Box(val type: String, val size: Long, val header: Long)

    /** A box at a position: [box] when readable, else [need] is at least 0 for more bytes, below 0 for an invalid header. */
    private class Read(val box: Box?, val need: Long)

    fun find(head: ByteArray): FirstFragmentInfo {
        if (head.size >= 8 && (0 until 8).all { head[it] == 0.toByte() }) return bad(FragmentStatus.ZeroFilled, "head is zero-filled")
        var pos = 0L
        while (true) {
            val read = readBox(head, pos)
            val box = read.box ?: return if (read.need < 0) bad(FragmentStatus.Invalid, "bad box header at $pos") else more(read.need)
            when (box.type) {
                "moof" -> {
                    if (box.size < 16 || box.size > MAX_FRAGMENT_BYTES) return bad(FragmentStatus.Invalid, "moof size ${box.size} unsupported")
                    val moofEnd = pos + box.size
                    if (moofEnd > head.size) return more(moofEnd + 8)
                    if (child(head, (pos + box.header).toInt(), (box.size - box.header).toInt(), "traf") == null) return bad(FragmentStatus.Invalid, "moof without traf")
                    val after = readBox(head, moofEnd)
                    val next = after.box ?: return if (after.need < 0) bad(FragmentStatus.Invalid, "bad box after moof") else more(after.need)
                    if (next.type != "mdat") return bad(FragmentStatus.Invalid, "box after moof is '${next.type}', not mdat")
                    val end = moofEnd + next.size
                    if (end - pos > MAX_FRAGMENT_BYTES) return bad(FragmentStatus.Invalid, "first fragment of ${end - pos} bytes exceeds the cap")
                    return FirstFragmentInfo(if (end > head.size) FragmentStatus.NeedMore else FragmentStatus.Found, pos, end - pos, end)
                }
                "mdat" -> return bad(FragmentStatus.Invalid, "mdat before any moof")
            }
            if (box.size > MAX_SKIPPED_BOX) return bad(FragmentStatus.Invalid, "box '${box.type}' of ${box.size} bytes before the first moof")
            pos += box.size
        }
    }

    /** `baseMediaDecodeTime` of the first `moof/traf/tfdt`, in the track's timescale; null when the head doesn't hold a complete first `moof` with one. */
    fun baseMediaDecodeTime(head: ByteArray): ULong? {
        var pos = 0L
        while (true) {
            val box = readBox(head, pos).box ?: return null
            if (pos + box.size > head.size || box.type == "mdat") return null
            if (box.type == "moof") {
                val traf = child(head, (pos + box.header).toInt(), (box.size - box.header).toInt(), "traf") ?: return null
                val tfdt = child(head, traf.first, traf.second, "tfdt") ?: return null
                if (tfdt.second < 8) return null
                return if (head[tfdt.first] == 1.toByte()) {
                    if (tfdt.second >= 12) u64(head, tfdt.first + 4) else null
                } else {
                    u32(head, tfdt.first + 4).toULong()
                }
            }
            pos += box.size
        }
    }

    /** The media timescale (`moov/trak/mdia/mdhd`) of an init segment, or null. */
    fun timescale(init: ByteArray): Long? {
        var pos = 0L
        while (true) {
            val box = readBox(init, pos).box ?: return null
            if (pos + box.size > init.size) return null
            if (box.type == "moov") {
                val trak = child(init, (pos + box.header).toInt(), (box.size - box.header).toInt(), "trak") ?: return null
                val mdia = child(init, trak.first, trak.second, "mdia") ?: return null
                val mdhd = child(init, mdia.first, mdia.second, "mdhd") ?: return null
                val at = if (mdhd.second > 0 && init[mdhd.first] == 1.toByte()) 20 else 12
                if (mdhd.second < at + 4) return null
                return u32(init, mdhd.first + at).takeIf { it != 0L }
            }
            pos += box.size
        }
    }

    /** The real start of a chunk on the tfdt axis, in seconds, from an init segment and the chunk's head. Null when either isn't understood. */
    fun tfdtSeconds(init: ByteArray, chunkHead: ByteArray): Double? {
        val scale = timescale(init) ?: return null
        val t = baseMediaDecodeTime(chunkHead) ?: return null
        return t.toDouble() / scale.toDouble()
    }

    private fun readBox(s: ByteArray, pos: Long): Read {
        if (pos + 8 > s.size) return Read(null, pos + 16)
        val p = pos.toInt()
        var size = u32(s, p)
        for (i in 4 until 8) if (s[p + i].toInt() !in 0x20..0x7E) return Read(null, -1)
        val type = String(s, p + 4, 4, Charsets.US_ASCII)
        var header = 8L
        if (size == 1L) {
            header = 16
            if (pos + 16 > s.size) return Read(null, pos + 16)
            val big = u64(s, p + 8)
            if (big > (MAX_FRAGMENT_BYTES * 4).toULong()) return Read(null, -1)
            size = big.toLong()
        }
        if (size < header) return Read(null, -1)                                     // also a size 0 box: an unbounded box isn't a first fragment
        return Read(Box(type, size, header), 0)
    }

    /** Body (start, length) of the first direct child of [type] in `s[start, start + length)`. */
    private fun child(s: ByteArray, start: Int, length: Int, type: String): Pair<Int, Int>? {
        val end = start + length
        var p = start
        while (p + 8 <= end) {
            val size = u32(s, p)
            if (size < 8 || p + size > end) return null
            if (String(s, p + 4, 4, Charsets.ISO_8859_1) == type) return (p + 8) to (size.toInt() - 8)
            p += size.toInt()
        }
        return null
    }

    private fun bad(status: FragmentStatus, error: String) = FirstFragmentInfo(status, 0, 0, 0, error)
    private fun more(required: Long) = FirstFragmentInfo(FragmentStatus.NeedMore, 0, 0, required)
}

internal fun u32(s: ByteArray, at: Int): Long =
    ((s[at].toLong() and 0xFF) shl 24) or ((s[at + 1].toLong() and 0xFF) shl 16) or ((s[at + 2].toLong() and 0xFF) shl 8) or (s[at + 3].toLong() and 0xFF)

internal fun u64(s: ByteArray, at: Int): ULong = (u32(s, at).toULong() shl 32) or u32(s, at + 4).toULong()
