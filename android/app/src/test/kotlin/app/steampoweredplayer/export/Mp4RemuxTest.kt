// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.io.File
import java.nio.ByteBuffer
import java.nio.file.Files
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The fMP4 reader and the MP4 writer on hand-made boxes shaped like Steam's. On real files they're checked against ffmpeg by RealLosslessTest. */
class Mp4RemuxTest {
    private val dir: File = Files.createTempDirectory("spp-remux").toFile()

    private fun box(type: String, body: DataOutputStream.() -> Unit): ByteArray = Mp4Remux.box(type, body)

    private val stsd = box("stsd") { writeInt(0); writeInt(1); write(box("hvc1") { write(ByteArray(40) { it.toByte() }) }) }

    /** Timescale 1 MHz, edit list starting at 33333, as Steam's picture init segment. */
    private fun init(timescale: Int, editStart: Int) = box("ftyp") { write(ByteArray(20)) } + box("moov") {
        write(box("mvhd") { writeInt(0); writeInt(0); writeInt(0); writeInt(1000); write(ByteArray(84)) })
        write(
            box("trak") {
                write(box("tkhd") { writeInt(3); write(ByteArray(80)) })
                write(box("edts") { write(box("elst") { writeInt(0); writeInt(1); writeInt(0); writeInt(editStart); writeInt(0x10000) }) })
                write(
                    box("mdia") {
                        write(box("mdhd") { writeInt(0); writeInt(0); writeInt(0); writeInt(timescale); writeInt(0); writeShort(0x55C4); writeShort(0) })
                        write(box("hdlr") { write(ByteArray(25)) })
                        write(
                            box("minf") {
                                write(box("vmhd") { write(ByteArray(12)) })
                                write(box("dinf") { write(ByteArray(8)) })
                                write(box("stbl") { write(stsd) })
                            },
                        )
                    },
                )
            },
        )
    }

    /** One fragment per sample: moof + mdat, with a first-sample-flags sync on [key], as Steam's. */
    private fun fragment(dts: Long, cts: Int, duration: Int, payload: ByteArray, key: Boolean): ByteArray {
        fun moof(dataOffset: Int) = box("moof") {
            write(box("mfhd") { writeInt(0); writeInt(1) })
            write(
                box("traf") {
                    write(box("tfhd") { writeInt(0x020038); writeInt(1); writeInt(duration); writeInt(payload.size); writeInt(0x01010000) })
                    write(box("tfdt") { writeInt(0x01000000); writeLong(dts) })
                    write(
                        box("trun") {
                            writeInt(if (key) 0x805 else 0x801)
                            writeInt(1)
                            writeInt(dataOffset)
                            if (key) writeInt(0x02000000)
                            writeInt(cts)
                        },
                    )
                },
            )
        }
        val size = moof(0).size
        return moof(size + 8) + box("mdat") { write(payload) }
    }

    private fun write(name: String, bytes: ByteArray) = File(dir, name).also { it.writeBytes(bytes) }

    /** P B B in decode order; pts on ffmpeg's axis = dts + cts − 33333. */
    private fun videoChunk(name: String, base: Long): File {
        val out = ByteArrayOutputStream()
        out.write(fragment(base, 50_000, 10_000, byteArrayOf(1, 1, 1), true))
        out.write(fragment(base + 10_000, 70_000, 10_000, byteArrayOf(2, 2), false))
        out.write(fragment(base + 20_000, 40_000, 10_000, byteArrayOf(3), false))
        return write(name, out.toByteArray())
    }

    @Test fun the_reader_gets_each_samples_place_and_times_from_the_moof() {
        val init = InitSegment.parse(init(1_000_000, 33_333))
        assertEquals(1_000_000, init.timescale)
        assertEquals(33_333, init.editMediaTime)
        val file = videoChunk("chunk-stream0-00001.m4s", 1_000_000)
        val samples = Fmp4Chunk.samples(file, init)
        assertEquals(listOf(3, 2, 1), samples.map { it.size })
        assertEquals(listOf(true, false, false), samples.map { it.key })
        val bytes = file.readBytes()
        assertEquals(1, bytes[samples[0].offset.toInt()].toInt())
        assertEquals(2, bytes[samples[1].offset.toInt()].toInt())
        assertEquals(3, bytes[samples[2].offset.toInt()].toInt())
        val seconds = Mp4Track(init, samples, true).sourceSeconds()
        assertEquals(1.016667, seconds[0], 1e-6)
        assertEquals(1.046667, seconds[1], 1e-6)
        assertEquals(1.026667, seconds[2], 1e-6)
    }

    @Test fun frame_tables_are_in_presentation_order_and_a_missing_chunk_is_null() {
        write("init-stream0.m4s", init(1_000_000, 33_333))
        videoChunk("chunk-stream0-00001.m4s", 1_000_000)
        val tables = FrameTables(dir)
        assertArrayEquals(doubleArrayOf(1.016667, 1.026667, 1.046667), tables.of(1), 1e-6)
        assertNull(tables.of(2))
    }

    private fun boxOf(d: ByteArray, type: String): ByteArray? {
        val name = type.toByteArray()
        for (i in 4..d.size - 4) {
            if (d[i] == name[0] && d[i + 1] == name[1] && d[i + 2] == name[2] && d[i + 3] == name[3]) {
                val size = ByteBuffer.wrap(d, i - 4, 4).int
                return d.copyOfRange(i - 4, i - 4 + size)
            }
        }
        return null
    }

    @Test fun the_file_holds_the_samples_unchanged_steams_stsd_and_the_edit_lists() {
        val videoInit = InitSegment.parse(init(1_000_000, 33_333))
        val audioInit = InitSegment.parse(init(48_000, 0))
        val video = Mp4Track(videoInit, Fmp4Chunk.samples(videoChunk("v.m4s", 1_000_000), videoInit), true)
        val audioFile = write("a.m4s", fragment(48_000, 0, 1024, byteArrayOf(9, 9), true))
        val audio = Mp4Track(audioInit, Fmp4Chunk.samples(audioFile, audioInit), false)
        val out = File(dir, "out.mp4")
        val edits = listOf(EditEntry(600, 50_000))
        Mp4Remux.write(out, video, audio, edits, listOf(EditEntry(600, 0)), listOf(OutputChapter(0.0, 1.0, "Kill")))

        val d = out.readBytes()
        assertArrayEquals(stsd, boxOf(d, "stsd"))
        val mdat = boxOf(d, "mdat")!!
        assertEquals(setOf<Byte>(1, 2, 3, 9), mdat.drop(8).toSet())
        assertEquals(8 + 8, mdat.size)
        assertNotNull(boxOf(d, "ctts"))
        val elst = boxOf(d, "elst")!!
        assertEquals(600, ByteBuffer.wrap(elst, 16, 4).int)
        assertEquals(50_000, ByteBuffer.wrap(elst, 20, 4).int)
        assertEquals(600, ByteBuffer.wrap(boxOf(d, "mvhd")!!, 8 + 16, 4).int)
        assertEquals(600, ByteBuffer.wrap(boxOf(d, "tkhd")!!, 8 + 20, 4).int)
        val stss = boxOf(d, "stss")!!
        assertEquals(1, ByteBuffer.wrap(stss, 12, 4).int)
        assertEquals(1, ByteBuffer.wrap(stss, 16, 4).int)
        assertTrue(String(boxOf(d, "chpl")!!, Charsets.ISO_8859_1).contains("Kill"))
        // stco points at each stored run's first byte.
        val stco = boxOf(d, "stco")!!
        val offset = ByteBuffer.wrap(stco, 16, 4).int
        assertEquals(1, d[offset].toInt())
    }

    @Test fun values_past_32_bits_use_a_version_1_edit_list() {
        val elst = boxOf(Mp4Remux.edts(listOf(EditEntry(100, 3_000_000_000))), "elst")!!
        assertEquals(1, elst[8].toInt())
        assertEquals(3_000_000_000, ByteBuffer.wrap(elst, 24, 8).long)
        val small = boxOf(Mp4Remux.edts(listOf(EditEntry(100, -1))), "elst")!!
        assertEquals(0, small[8].toInt())
        assertEquals(-1, ByteBuffer.wrap(small, 20, 4).int)
        assertFalse(small.isEmpty())
    }

    @Test fun chapters_stop_at_what_the_box_can_hold() {
        val many = List(300) { OutputChapter(it.toDouble(), it + 1.0, "c$it") }
        assertEquals(255, Mp4Remux.chpl(many)[16].toInt() and 0xFF)
    }
}
