// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import app.steampoweredplayer.data.FolderNames
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.channels.FileChannel
import java.nio.file.StandardOpenOption

/** A box inside a byte array: its body is `[body, end)`. */
internal class BoxRef(val type: String, val start: Int, val body: Int, val end: Int)

internal object Boxes {
    fun children(d: ByteArray, start: Int, end: Int): List<BoxRef> {
        val out = ArrayList<BoxRef>()
        var i = start
        while (i + 8 <= end) {
            var size = ByteBuffer.wrap(d, i, 4).int.toLong() and 0xFFFFFFFFL
            var header = 8
            if (size == 1L) {
                size = ByteBuffer.wrap(d, i + 8, 8).long
                header = 16
            } else if (size == 0L) {
                size = (end - i).toLong()
            }
            if (size < header || i + size > end) throw IOException("damaged MP4 box")
            out.add(BoxRef(String(d, i + 4, 4, Charsets.ISO_8859_1), i, i + header, (i + size).toInt()))
            i += size.toInt()
        }
        return out
    }

    fun child(d: ByteArray, parent: BoxRef, type: String): BoxRef? = children(d, parent.body, parent.end).firstOrNull { it.type == type }
}

/** What a chunk's samples need from its stream's init segment. The init's own bytes go into the output, so the codec setup stays exactly Steam's. */
class InitSegment(
    val timescale: Long,
    /** The init's edit list start, in track ticks: it moves every sample on ffmpeg's and mpv's axis. */
    val editMediaTime: Long,
    /** Seconds of empty edit before that. */
    val emptyEditSeconds: Double,
    internal val tkhd: ByteArray,
    internal val mdhd: ByteArray,
    internal val hdlr: ByteArray,
    internal val mediaHeader: ByteArray,
    internal val dinf: ByteArray,
    internal val stsd: ByteArray,
    internal val defaultDuration: Long,
    internal val defaultSize: Long,
    internal val defaultFlags: Long,
) {
    companion object {
        fun parse(bytes: ByteArray): InitSegment {
            val top = Boxes.children(bytes, 0, bytes.size)
            val moov = top.firstOrNull { it.type == "moov" } ?: throw IOException("init segment without moov")
            val trak = Boxes.child(bytes, moov, "trak") ?: throw IOException("init segment without trak")
            val mvhd = Boxes.child(bytes, moov, "mvhd") ?: throw IOException("init segment without mvhd")
            val movieScale = ByteBuffer.wrap(bytes, mvhd.body + if (bytes[mvhd.body] == 1.toByte()) 20 else 12, 4).int.toLong() and 0xFFFFFFFFL
            val mdia = Boxes.child(bytes, trak, "mdia") ?: throw IOException("no mdia")
            val mdhd = Boxes.child(bytes, mdia, "mdhd") ?: throw IOException("no mdhd")
            val timescale = ByteBuffer.wrap(bytes, mdhd.body + if (bytes[mdhd.body] == 1.toByte()) 20 else 12, 4).int.toLong() and 0xFFFFFFFFL
            if (timescale == 0L) throw IOException("no timescale")
            val hdlr = Boxes.child(bytes, mdia, "hdlr") ?: throw IOException("no hdlr")
            val minf = Boxes.child(bytes, mdia, "minf") ?: throw IOException("no minf")
            val media = Boxes.child(bytes, minf, "vmhd") ?: Boxes.child(bytes, minf, "smhd") ?: throw IOException("no media header")
            val dinf = Boxes.child(bytes, minf, "dinf") ?: throw IOException("no dinf")
            val stbl = Boxes.child(bytes, minf, "stbl") ?: throw IOException("no stbl")
            val stsd = Boxes.child(bytes, stbl, "stsd") ?: throw IOException("no stsd")
            val tkhd = Boxes.child(bytes, trak, "tkhd") ?: throw IOException("no tkhd")

            var editStart = 0L
            var empty = 0.0
            Boxes.child(bytes, trak, "edts")?.let { Boxes.child(bytes, it, "elst") }?.let { elst ->
                val b = ByteBuffer.wrap(bytes, elst.body, elst.end - elst.body)
                val version = b.get().toInt()
                b.position(b.position() + 3)
                val count = b.int
                for (i in 0 until count) {
                    val duration = if (version == 1) b.long else b.int.toLong() and 0xFFFFFFFFL
                    val time = if (version == 1) b.long else b.int.toLong()
                    b.int
                    if (time == -1L) {
                        empty += duration.toDouble() / maxOf(1, movieScale)
                    } else {
                        editStart = time
                        break
                    }
                }
            }
            var defDuration = 0L
            var defSize = 0L
            var defFlags = 0L
            Boxes.child(bytes, moov, "mvex")?.let { Boxes.child(bytes, it, "trex") }?.let { trex ->
                val b = ByteBuffer.wrap(bytes, trex.body + 12, 12)
                defDuration = b.int.toLong() and 0xFFFFFFFFL
                defSize = b.int.toLong() and 0xFFFFFFFFL
                defFlags = b.int.toLong() and 0xFFFFFFFFL
            }
            fun copy(b: BoxRef) = bytes.copyOfRange(b.start, b.end)
            return InitSegment(timescale, editStart, empty, copy(tkhd), copy(mdhd), copy(hdlr), copy(media), copy(dinf), copy(stsd), defDuration, defSize, defFlags)
        }
    }
}

/** One stored sample of a chunk file. [dts] is the fragment's decode time, [cts] its composition offset, both in track ticks. */
class Fmp4Sample(val file: File, val offset: Long, val size: Int, val dts: Long, val cts: Int, val key: Boolean)

/** Reads the sample table of Steam's fragmented chunks from their `moof` boxes; no sample data is read. */
object Fmp4Chunk {
    fun samples(file: File, init: InitSegment): List<Fmp4Sample> {
        val out = ArrayList<Fmp4Sample>(256)
        RandomAccessFile(file, "r").use { f ->
            val length = f.length()
            val head = ByteArray(16)
            var pos = 0L
            while (pos + 8 <= length) {
                f.seek(pos)
                f.readFully(head, 0, 8)
                var size = ByteBuffer.wrap(head, 0, 4).int.toLong() and 0xFFFFFFFFL
                val type = String(head, 4, 4, Charsets.ISO_8859_1)
                var header = 8
                if (size == 1L) {
                    f.readFully(head, 8, 8)
                    size = ByteBuffer.wrap(head, 8, 8).long
                    header = 16
                } else if (size == 0L) {
                    size = length - pos
                }
                if (size < header || pos + size > length) throw IOException("damaged chunk ${file.name}")
                if (type == "moof") {
                    if (size > MAX_MOOF_BYTES) throw IOException("moof too large in ${file.name}")
                    val moof = ByteArray((size - header).toInt())
                    f.seek(pos + header)
                    f.readFully(moof)
                    readMoof(moof, pos, file, init, out)
                }
                pos += size
            }
        }
        return out
    }

    private fun readMoof(d: ByteArray, moofStart: Long, file: File, init: InitSegment, out: MutableList<Fmp4Sample>) {
        for (traf in Boxes.children(d, 0, d.size).filter { it.type == "traf" }) {
            val boxes = Boxes.children(d, traf.body, traf.end)
            val tfhd = boxes.firstOrNull { it.type == "tfhd" } ?: throw IOException("traf without tfhd")
            val tfdt = boxes.firstOrNull { it.type == "tfdt" } ?: throw IOException("traf without tfdt")
            val h = ByteBuffer.wrap(d, tfhd.body, tfhd.end - tfhd.body)
            val tfFlags = h.int and 0xFFFFFF
            h.int
            var base = moofStart
            if (tfFlags and 0x1 != 0) base = h.long
            if (tfFlags and 0x2 != 0) h.int
            val defDuration = if (tfFlags and 0x8 != 0) h.int.toLong() and 0xFFFFFFFFL else init.defaultDuration
            val defSize = if (tfFlags and 0x10 != 0) h.int.toLong() and 0xFFFFFFFFL else init.defaultSize
            val defFlags = if (tfFlags and 0x20 != 0) h.int.toLong() and 0xFFFFFFFFL else init.defaultFlags
            val t = ByteBuffer.wrap(d, tfdt.body, tfdt.end - tfdt.body)
            val version = t.int ushr 24
            var dts = if (version == 1) t.long else t.int.toLong() and 0xFFFFFFFFL
            var nextOffset = -1L
            for (trun in boxes.filter { it.type == "trun" }) {
                val r = ByteBuffer.wrap(d, trun.body, trun.end - trun.body)
                val vf = r.int
                val flags = vf and 0xFFFFFF
                val count = r.int
                val dataOffset = if (flags and 0x1 != 0) r.int.toLong() else null
                val firstFlags = if (flags and 0x4 != 0) r.int.toLong() and 0xFFFFFFFFL else null
                var at = when {
                    dataOffset != null -> base + dataOffset
                    nextOffset >= 0 -> nextOffset
                    else -> base
                }
                for (i in 0 until count) {
                    val duration = if (flags and 0x100 != 0) r.int.toLong() and 0xFFFFFFFFL else defDuration
                    val size = if (flags and 0x200 != 0) r.int.toLong() and 0xFFFFFFFFL else defSize
                    val sampleFlags = if (flags and 0x400 != 0) r.int.toLong() and 0xFFFFFFFFL else if (i == 0 && firstFlags != null) firstFlags else defFlags
                    val cts = if (flags and 0x800 != 0) r.int else 0
                    out.add(Fmp4Sample(file, at, size.toInt(), dts, cts, sampleFlags and SAMPLE_NON_SYNC == 0L))
                    at += size
                    dts += duration
                }
                nextOffset = at
            }
        }
    }

    private const val MAX_MOOF_BYTES = 16L * 1024 * 1024
    private const val SAMPLE_NON_SYNC = 0x00010000L
}

/**
 * The real time of each frame of a recording's chunks on ffmpeg's and mpv's axis, read from their `moof` boxes, for frame stepping. Keeps the last few chunks. Thread-safe.
 * A chunk that isn't there or can't be read gives null.
 */
class FrameTables(private val folder: File) {
    private val cache = object : LinkedHashMap<Int, DoubleArray>(CACHED_CHUNKS, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<Int, DoubleArray>?) = size > CACHED_CHUNKS
    }
    private var init: InitSegment? = null

    @Synchronized
    fun of(chunk: Int): DoubleArray? {
        cache[chunk]?.let { return it }
        return try {
            val parsed = init ?: InitSegment.parse(File(folder, FolderNames.initFile(0)).readBytes()).also { init = it }
            val file = File(folder, FolderNames.chunkFile(0, chunk))
            if (!file.exists()) return null
            Mp4Track(parsed, Fmp4Chunk.samples(file, parsed), true).sourceSeconds().also { it.sort() }.also { cache[chunk] = it }
        } catch (_: IOException) {
            null
        }
    }

    private companion object {
        const val CACHED_CHUNKS = 8
    }
}

/** One track to store: samples in decode order, with their init segment, and the length of its last sample in ticks. */
class Mp4Track(val init: InitSegment, val samples: List<Fmp4Sample>, val isVideo: Boolean) {
    /** Each sample's pts in seconds on ffmpeg's and mpv's axis (the init's edit list applied). */
    fun sourceSeconds(): DoubleArray = DoubleArray(samples.size) {
        (samples[it].dts + samples[it].cts - init.editMediaTime).toDouble() / init.timescale + init.emptyEditSeconds
    }

    /** The decode time of each sample in the output, counted from the first. A gap past what `stts` can hold is closed. */
    internal fun decodeTicks(): LongArray {
        val out = LongArray(samples.size)
        for (i in 1 until samples.size) out[i] = out[i - 1] + (samples[i].dts - samples[i - 1].dts).coerceIn(0, MAX_DELTA)
        return out
    }

    /** Each sample's composition time on the output's media axis, edit lists ignored: what an edit list's media time counts. */
    fun mediaTicks(): LongArray {
        val decode = decodeTicks()
        return LongArray(samples.size) { decode[it] + samples[it].cts }
    }

    private companion object {
        const val MAX_DELTA = 0x7FFFFFFFL
    }
}

/**
 * Writes a plain MP4 from fragmented samples: no sample byte changes and `stsd` is the init's own, so a lossless export stays bit for bit what Steam wrote. The `moov` goes
 * first, with the edit lists that hide what isn't shown. Media3's muxer isn't used here: it re-derives the codec setup and writes its own edit lists.
 */
object Mp4Remux {
    const val MOVIE_TIMESCALE = 6_000_000L

    fun timescales(video: Mp4Track, audio: Mp4Track) = Mp4Timescales(MOVIE_TIMESCALE, video.init.timescale, audio.init.timescale)

    /** Stops between samples when [cancelled] says so; the caller removes the unfinished file. [progress] goes from 0 to 1. */
    fun write(
        output: File,
        video: Mp4Track,
        audio: Mp4Track,
        videoEdits: List<EditEntry>,
        audioEdits: List<EditEntry>,
        chapters: List<OutputChapter> = emptyList(),
        cancelled: () -> Boolean = { false },
        progress: (Double) -> Unit = {},
    ) {
        val groups = interleave(video, audio)
        val payload = groups.sumOf { g -> g.samples.sumOf { it.size.toLong() } }
        val ftyp = box("ftyp") { write("isom".toByteArray()); writeInt(0x200); write("isomiso2mp41".toByteArray()) }
        // The chunk offsets depend on the moov's size, and whether they fit 32 bits on the total: two passes.
        val wide = payload + ftyp.size > MAX_32_BIT_OFFSET
        val moovSize = moov(video, audio, videoEdits, audioEdits, chapters, groups, 0, wide).size
        val mdatHeader = if (payload + 8 > MAX_32_BIT_OFFSET) 16 else 8
        val dataStart = ftyp.size.toLong() + moovSize + mdatHeader
        val moov = moov(video, audio, videoEdits, audioEdits, chapters, groups, dataStart, wide)
        check(moov.size == moovSize)

        FileChannel.open(output.toPath(), StandardOpenOption.CREATE, StandardOpenOption.WRITE, StandardOpenOption.TRUNCATE_EXISTING).use { out ->
            writeAll(out, ByteBuffer.wrap(ftyp))
            writeAll(out, ByteBuffer.wrap(moov))
            val header = ByteBuffer.allocate(mdatHeader)
            if (mdatHeader == 16) {
                header.putInt(1).put("mdat".toByteArray()).putLong(payload + 16)
            } else {
                header.putInt((payload + 8).toInt()).put("mdat".toByteArray())
            }
            header.flip()
            writeAll(out, header)
            var done = 0L
            var openFile: File? = null
            var input: FileChannel? = null
            try {
                for (group in groups) {
                    if (cancelled()) throw kotlinx.coroutines.CancellationException()
                    for (s in group.samples) {
                        if (s.file !== openFile) {
                            input?.close()
                            input = FileChannel.open(s.file.toPath(), StandardOpenOption.READ)
                            openFile = s.file
                        }
                        var at = s.offset
                        var left = s.size.toLong()
                        while (left > 0) {
                            val n = input!!.transferTo(at, left, out)
                            if (n <= 0) throw IOException("chunk ended early: ${s.file.name}")
                            at += n
                            left -= n
                        }
                        done += s.size
                    }
                    progress(done.toDouble() / maxOf(1, payload))
                }
            } finally {
                input?.close()
            }
        }
    }

    private fun writeAll(out: FileChannel, b: ByteBuffer) {
        while (b.hasRemaining()) out.write(b)
    }

    /** A run of one track's samples stored together: the file's `stco` chunks. */
    internal class Group(val track: Int, val samples: List<Fmp4Sample>, var offset: Long = 0)

    /** About half a second of each track in turn, by time, so a player reading the file front to back has both close by. */
    internal fun interleave(video: Mp4Track, audio: Mp4Track): List<Group> {
        val out = ArrayList<Group>()
        var v = 0
        var a = 0
        val vt = video.sourceSeconds()
        val at = audio.sourceSeconds()
        while (v < video.samples.size || a < audio.samples.size) {
            val nextV = if (v < vt.size) vt[v] else Double.MAX_VALUE
            val nextA = if (a < at.size) at[a] else Double.MAX_VALUE
            if (nextV <= nextA) {
                val start = v
                while (v < vt.size && vt[v] < nextV + GROUP_SECONDS) v++
                if (v == start) v++
                out.add(Group(0, video.samples.subList(start, v)))
            } else {
                val start = a
                while (a < at.size && at[a] < nextA + GROUP_SECONDS) a++
                if (a == start) a++
                out.add(Group(1, audio.samples.subList(start, a)))
            }
        }
        return out
    }

    private fun moov(
        video: Mp4Track,
        audio: Mp4Track,
        videoEdits: List<EditEntry>,
        audioEdits: List<EditEntry>,
        chapters: List<OutputChapter>,
        groups: List<Group>,
        dataStart: Long,
        wide: Boolean,
    ): ByteArray {
        var at = dataStart
        for (g in groups) {
            g.offset = at
            at += g.samples.sumOf { it.size.toLong() }
        }
        val movieDuration = maxOf(videoEdits.sumOf { it.duration }, audioEdits.sumOf { it.duration })
        return box("moov") {
            write(mvhd(movieDuration))
            write(trak(1, video, videoEdits, groups.filter { it.track == 0 }, wide))
            write(trak(2, audio, audioEdits, groups.filter { it.track == 1 }, wide))
            if (chapters.isNotEmpty()) write(box("udta") { write(chpl(chapters)) })
        }
    }

    private fun mvhd(duration: Long): ByteArray = box("mvhd") {
        val longForm = duration > MAX_U32
        writeInt(if (longForm) 0x01000000 else 0)
        if (longForm) {
            writeLong(0); writeLong(0)
        } else {
            writeInt(0); writeInt(0)
        }
        writeInt(MOVIE_TIMESCALE.toInt())
        if (longForm) writeLong(duration) else writeInt(duration.toInt())
        writeInt(0x00010000)                                                          // rate 1.0
        writeShort(0x0100)                                                            // volume 1.0
        write(ByteArray(10))
        for (v in intArrayOf(0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000)) writeInt(v)
        write(ByteArray(24))
        writeInt(3)                                                                   // next track id
    }

    private fun trak(id: Int, track: Mp4Track, edits: List<EditEntry>, groups: List<Group>, wide: Boolean): ByteArray = box("trak") {
        write(tkhd(id, track.init.tkhd, edits.sumOf { it.duration }))
        write(edts(edits))
        write(box("mdia") {
            write(mdhd(track))
            write(track.init.hdlr)
            write(box("minf") {
                write(track.init.mediaHeader)
                write(track.init.dinf)
                write(box("stbl") {
                    write(track.init.stsd)
                    write(stts(track))
                    if (track.isVideo) {
                        write(ctts(track))
                        write(stss(track))
                    }
                    write(stsc(groups))
                    write(stsz(track))
                    write(chunkOffsets(groups, wide))
                })
            })
        })
    }

    /** Steam's own header with its size, flags and track id kept, the duration set. A duration past 32 bits needs the version 1 layout. */
    private fun tkhd(id: Int, init: ByteArray, duration: Long): ByteArray {
        val src = ByteBuffer.wrap(init)
        src.position(8)
        val versionFlags = src.int
        val v1 = versionFlags ushr 24 == 1
        src.position(src.position() + if (v1) 32 else 20)                             // times, track id, reserved, duration
        val tail = ByteArray(src.remaining())
        src.get(tail)
        return box("tkhd") {
            val flags = 3                                                             // enabled, in movie
            val longForm = duration > MAX_U32
            writeInt((if (longForm) 0x01000000 else 0) or flags)
            if (longForm) {
                writeLong(0); writeLong(0)
            } else {
                writeInt(0); writeInt(0)
            }
            writeInt(id)
            writeInt(0)
            if (longForm) writeLong(duration) else writeInt(duration.toInt())
            write(tail)
        }
    }

    private fun mdhd(track: Mp4Track): ByteArray {
        val init = track.init.mdhd
        val language = ByteBuffer.wrap(init, init.size - 4, 2).short.toInt()
        val decode = track.decodeTicks()
        val last = decode.lastOrNull() ?: 0
        val lastDelta = if (decode.size > 1) decode[decode.size - 1] - decode[decode.size - 2] else 0
        val duration = last + lastDelta
        return box("mdhd") {
            writeInt(0x01000000)
            writeLong(0); writeLong(0)
            writeInt(track.init.timescale.toInt())
            writeLong(duration)
            writeShort(language)
            writeShort(0)
        }
    }

    internal fun edts(edits: List<EditEntry>): ByteArray {
        val wide = edits.any { it.duration > MAX_U32 || it.mediaTime > Int.MAX_VALUE }
        return box("edts") {
            write(box("elst") {
                writeInt(if (wide) 0x01000000 else 0)
                writeInt(edits.size)
                for (e in edits) {
                    if (wide) {
                        writeLong(e.duration)
                        writeLong(e.mediaTime)
                    } else {
                        writeInt(e.duration.toInt())
                        writeInt(e.mediaTime.toInt())
                    }
                    writeInt(0x00010000)
                }
            })
        }
    }

    private fun stts(track: Mp4Track): ByteArray {
        val decode = track.decodeTicks()
        val deltas = LongArray(decode.size) { i -> if (i + 1 < decode.size) decode[i + 1] - decode[i] else if (i > 0) decode[i] - decode[i - 1] else 0 }
        val runs = runs(deltas)
        return box("stts") {
            writeInt(0)
            writeInt(runs.size)
            for ((count, value) in runs) {
                writeInt(count)
                writeInt(value.toInt())
            }
        }
    }

    private fun ctts(track: Mp4Track): ByteArray {
        val runs = runs(LongArray(track.samples.size) { track.samples[it].cts.toLong() })
        return box("ctts") {
            writeInt(0x01000000)                                                      // version 1: signed offsets
            writeInt(runs.size)
            for ((count, value) in runs) {
                writeInt(count)
                writeInt(value.toInt())
            }
        }
    }

    private fun stss(track: Mp4Track): ByteArray {
        val keys = track.samples.indices.filter { track.samples[it].key }
        return box("stss") {
            writeInt(0)
            writeInt(keys.size)
            for (k in keys) writeInt(k + 1)
        }
    }

    private fun stsc(groups: List<Group>): ByteArray {
        val entries = ArrayList<Pair<Int, Int>>()
        groups.forEachIndexed { i, g -> if (entries.isEmpty() || entries.last().second != g.samples.size) entries.add((i + 1) to g.samples.size) }
        return box("stsc") {
            writeInt(0)
            writeInt(entries.size)
            for ((first, count) in entries) {
                writeInt(first)
                writeInt(count)
                writeInt(1)
            }
        }
    }

    private fun stsz(track: Mp4Track): ByteArray = box("stsz") {
        writeInt(0)
        writeInt(0)
        writeInt(track.samples.size)
        for (s in track.samples) writeInt(s.size)
    }

    private fun chunkOffsets(groups: List<Group>, wide: Boolean): ByteArray = box(if (wide) "co64" else "stco") {
        writeInt(0)
        writeInt(groups.size)
        for (g in groups) if (wide) writeLong(g.offset) else writeInt(g.offset.toInt())
    }

    /** Nero chapters, which ffmpeg and mpv read. At most 255 fit. */
    internal fun chpl(chapters: List<OutputChapter>): ByteArray = box("chpl") {
        val kept = chapters.take(MAX_CHAPTERS)
        writeInt(0x01000000)
        writeInt(0)
        writeByte(kept.size)
        for (c in kept) {
            writeLong(Math.round(c.start * 10_000_000))
            val title = c.title.toByteArray().let { if (it.size > 255) it.copyOf(255) else it }
            writeByte(title.size)
            write(title)
        }
    }

    private fun runs(values: LongArray): List<Pair<Int, Long>> {
        val out = ArrayList<Pair<Int, Long>>()
        for (v in values) {
            if (out.isNotEmpty() && out.last().second == v) out[out.lastIndex] = (out.last().first + 1) to v else out.add(1 to v)
        }
        return out
    }

    internal fun box(type: String, body: DataOutputStream.() -> Unit): ByteArray {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { it.body() }
        val inner = bytes.toByteArray()
        return ByteBuffer.allocate(inner.size + 8).putInt(inner.size + 8).put(type.toByteArray(Charsets.ISO_8859_1)).put(inner).array()
    }

    private const val MAX_U32 = 0xFFFFFFFFL
    private const val MAX_32_BIT_OFFSET = 0xFFFFFFF0L - (1L shl 22)                   // leaves room for the moov itself
    private const val GROUP_SECONDS = 0.5
    private const val MAX_CHAPTERS = 255
}
