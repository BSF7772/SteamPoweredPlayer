// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.edit

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaExtractor
import android.media.MediaFormat
import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.SessionMedia
import app.steampoweredplayer.export.ExportSpan
import app.steampoweredplayer.export.ExportPlan
import app.steampoweredplayer.export.Fmp4Chunk
import app.steampoweredplayer.export.InitSegment
import app.steampoweredplayer.export.Mp4Track
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.nio.ByteOrder
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin

/** A stream's init segment and chunks as one file, without copying them. */
class ConcatDataSource(files: List<File>) : android.media.MediaDataSource() {
    private val parts = files.map { RandomAccessFile(it, "r") }
    private val starts = LongArray(parts.size)
    private val total: Long

    init {
        var at = 0L
        parts.forEachIndexed { i, f ->
            starts[i] = at
            at += f.length()
        }
        total = at
    }

    override fun getSize() = total

    override fun readAt(position: Long, buffer: ByteArray, offset: Int, size: Int): Int {
        if (position >= total) return -1
        var i = starts.binarySearch(position)
        if (i < 0) i = -i - 2
        val part = parts[i]
        val inside = position - starts[i]
        part.seek(inside)
        return part.read(buffer, offset, min(size.toLong(), part.length() - inside).toInt())
    }

    override fun close() = parts.forEach { it.close() }
}

/**
 * The Kotlin port's decode side: MediaExtractor and MediaCodec in place of the desktop's two ffmpeg processes (spike S8). Times come from the chunks' own tables, on the same
 * axis as ffmpeg's, because MediaExtractor doesn't treat Steam's edit list the same way on every Android version. Blocking: call off the main thread.
 */
object SyncDetector {
    const val MAX_SECONDS = 75.0

    /** The part of [span] the detector listens to: at most 75 s from its middle. */
    fun window(span: ExportSpan): ExportSpan {
        if (span.length <= MAX_SECONDS) return span
        val mid = (span.start + span.end) / 2
        return ExportSpan(mid - MAX_SECONDS / 2, mid + MAX_SECONDS / 2)
    }

    /** The chunks the detector reads; the caller makes sure they're on the phone. */
    fun chunks(media: SessionMedia, span: ExportSpan): List<Int> {
        val w = window(span)
        val plan = ExportPlan.of(media, w.start, w.end) ?: return emptyList()
        return media.chunks().filter { it in plan.firstChunk..plan.lastChunk }
    }

    fun detect(media: SessionMedia, span: ExportSpan, cancelled: () -> Boolean = { false }, progress: (Double) -> Unit = {}): AudioSync.Result {
        val w = window(span)
        val plan = ExportPlan.of(media, w.start, w.end) ?: throw IOException("Nothing to analyse in this range.")
        val folder = File(media.folder)
        val chunks = media.chunks().filter { it in plan.firstChunk..plan.lastChunk }
        val start = plan.start
        val length = plan.end - plan.start
        val audio = decodeAudio(folder, chunks, start, length, cancelled)
        progress(0.3)
        val video = decodeVideo(folder, chunks, start, length, cancelled) { progress(0.3 + it * 0.6) }
        val result = AudioSync.analyse(audio, video)
        progress(1.0)
        return result
    }

    private fun sources(folder: File, stream: Int, chunks: List<Int>): Pair<ConcatDataSource, Double> {
        val initFile = File(folder, FolderNames.initFile(stream))
        val files = listOf(initFile) + chunks.map { File(folder, FolderNames.chunkFile(stream, it)) }
        val init = InitSegment.parse(initFile.readBytes())
        val first = Mp4Track(init, Fmp4Chunk.samples(files[1], init), stream == 0).sourceSeconds().minOrNull() ?: throw IOException("empty chunk")
        return ConcatDataSource(files) to first
    }

    private fun track(ex: MediaExtractor, prefix: String): Int =
        (0 until ex.trackCount).firstOrNull { ex.getTrackFormat(it).getString(MediaFormat.KEY_MIME)!!.startsWith(prefix) } ?: throw IOException("no $prefix track")

    /** Mono float PCM at 16 kHz; sample 0 is [start] on the source axis. A buffer goes to its own time: gaps stay silent, small overlaps are overwritten. */
    private fun decodeAudio(folder: File, chunks: List<Int>, start: Double, length: Double, cancelled: () -> Boolean): FloatArray {
        val (source, firstSource) = sources(folder, 1, chunks)
        val ex = MediaExtractor()
        ex.setDataSource(source)
        val t = track(ex, "audio/")
        ex.selectTrack(t)
        val format = ex.getTrackFormat(t)
        val codec = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME)!!)
        codec.configure(format, null, null, 0)
        codec.start()
        var rate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE)
        var channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
        var pcm = FloatArray((length * rate).toInt() + rate)
        var written = 0
        var firstOut = Long.MIN_VALUE
        val info = MediaCodec.BufferInfo()
        var inputDone = false
        var outputDone = false
        try {
            while (!outputDone) {
                if (cancelled()) throw kotlinx.coroutines.CancellationException()
                if (!inputDone) {
                    val i = codec.dequeueInputBuffer(5000)
                    if (i >= 0) {
                        val n = ex.readSampleData(codec.getInputBuffer(i)!!, 0)
                        if (n < 0) {
                            codec.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                            inputDone = true
                        } else {
                            codec.queueInputBuffer(i, 0, n, ex.sampleTime, 0)
                            ex.advance()
                        }
                    }
                }
                val o = codec.dequeueOutputBuffer(info, 5000)
                if (o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    rate = codec.outputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                    channels = codec.outputFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                } else if (o >= 0) {
                    if (info.size > 0) {
                        if (firstOut == Long.MIN_VALUE) firstOut = info.presentationTimeUs
                        val samples = codec.getOutputBuffer(o)!!.order(ByteOrder.nativeOrder()).asShortBuffer()
                        val frames = info.size / 2 / channels
                        val seconds = firstSource + (info.presentationTimeUs - firstOut) / 1e6 - start
                        var pos = Math.round(seconds * rate).toInt()
                        if (pcm.size < pos + frames) pcm = pcm.copyOf(max(pcm.size * 2, pos + frames))
                        for (f in 0 until frames) {
                            var s = 0f
                            for (c in 0 until channels) s += samples.get(f * channels + c)
                            if (pos >= 0) pcm[pos] = s / channels / 32768f
                            pos++
                        }
                        written = max(written, pos)
                    }
                    codec.releaseOutputBuffer(o, false)
                    if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) outputDone = true
                }
            }
        } finally {
            codec.stop()
            codec.release()
            ex.release()
            source.close()
        }
        val total = min(written, (length * rate).toInt())
        return resample(pcm.copyOf(max(0, total)), rate, AudioSync.SR)
    }

    /** Windowed-sinc (Blackman) resampler, cutoff 0.45 of the lower rate. */
    fun resample(x: FloatArray, from: Int, to: Int): FloatArray {
        if (from == to) return x
        val ratio = to.toDouble() / from
        val n = (x.size * ratio).toInt()
        val fc = 0.45 * min(1.0, ratio)
        val half = 32
        val phases = 1024
        val table = Array(phases + 1) { p ->
            val frac = p / phases.toDouble()
            val hk = DoubleArray(2 * half)
            var ws = 0.0
            for (j in 0 until 2 * half) {
                val d = frac + (half - 1 - j)
                val sinc = if (d == 0.0) 2 * fc else sin(2 * PI * fc * d) / (PI * d)
                val wpos = (d + half) / (2.0 * half)
                val win = 0.42 - 0.5 * cos(2 * PI * wpos) + 0.08 * cos(4 * PI * wpos)
                hk[j] = sinc * win
                ws += hk[j]
            }
            for (j in hk.indices) hk[j] /= ws
            hk
        }
        val y = FloatArray(n)
        for (i in 0 until n) {
            val t = i / ratio
            val c = t.toInt()
            val hk = table[Math.round((t - c) * phases).toInt()]
            var s = 0.0
            val k0 = c - half + 1
            for (j in 0 until 2 * half) {
                val k = k0 + j
                if (k >= 0 && k < x.size) s += x[k] * hk[j]
            }
            y[i] = s.toFloat()
        }
        return y
    }

    /** Features from the luma plane, box-downscaled to 192x108. */
    private fun decodeVideo(folder: File, chunks: List<Int>, start: Double, length: Double, cancelled: () -> Boolean, progress: (Double) -> Unit): AudioSync.VideoFeatures {
        val (source, firstSource) = sources(folder, 0, chunks)
        val ex = MediaExtractor()
        ex.setDataSource(source)
        val t = track(ex, "video/")
        ex.selectTrack(t)
        val format = ex.getTrackFormat(t)
        val codec = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME)!!)
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
        codec.configure(format, null, null, 0)
        codec.start()
        val acc = AudioSync.VideoAccumulator()
        val info = MediaCodec.BufferInfo()
        var inputDone = false
        var outputDone = false
        var firstOut = Long.MAX_VALUE
        var w = 0
        var h = 0
        var colIdx = IntArray(0)
        var rowIdx = IntArray(0)
        var cnt = IntArray(0)
        val sums = IntArray(AudioSync.W * AudioSync.H)
        var row = ByteArray(0)
        try {
            while (!outputDone) {
                if (cancelled()) throw kotlinx.coroutines.CancellationException()
                if (!inputDone) {
                    val i = codec.dequeueInputBuffer(5000)
                    if (i >= 0) {
                        val n = ex.readSampleData(codec.getInputBuffer(i)!!, 0)
                        if (n < 0) {
                            codec.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                            inputDone = true
                        } else {
                            codec.queueInputBuffer(i, 0, n, ex.sampleTime, 0)
                            ex.advance()
                        }
                    }
                }
                val o = codec.dequeueOutputBuffer(info, 5000)
                if (o >= 0) {
                    if (firstOut == Long.MAX_VALUE && info.size > 0) firstOut = info.presentationTimeUs
                    val seconds = firstSource + (info.presentationTimeUs - firstOut) / 1e6 - start
                    if (info.size > 0 && seconds >= 0 && seconds < length) {
                        val img = codec.getOutputImage(o)!!
                        val crop = img.cropRect
                        if (w != crop.width() || h != crop.height()) {
                            w = crop.width()
                            h = crop.height()
                            colIdx = IntArray(w) { min(AudioSync.W - 1, it * AudioSync.W / w) }
                            rowIdx = IntArray(h) { min(AudioSync.H - 1, it * AudioSync.H / h) }
                            cnt = IntArray(AudioSync.W * AudioSync.H)
                            for (y in 0 until h) for (x in 0 until w) cnt[rowIdx[y] * AudioSync.W + colIdx[x]]++
                            row = ByteArray(w)
                        }
                        val plane = img.planes[0]
                        val yb = plane.buffer
                        val rs = plane.rowStride
                        val ps = plane.pixelStride
                        sums.fill(0)
                        for (y in 0 until h) {
                            val base = (crop.top + y) * rs + crop.left * ps
                            val r = rowIdx[y] * AudioSync.W
                            if (ps == 1) {
                                yb.position(base)
                                yb.get(row, 0, w)
                                for (x in 0 until w) sums[r + colIdx[x]] += row[x].toInt() and 0xFF
                            } else {
                                for (x in 0 until w) sums[r + colIdx[x]] += yb.get(base + x * ps).toInt() and 0xFF
                            }
                        }
                        val grey = ByteArray(AudioSync.W * AudioSync.H) { ((sums[it] + cnt[it] / 2) / cnt[it]).toByte() }
                        img.close()
                        acc.add(grey, seconds)
                        progress(min(1.0, seconds / length))
                    }
                    codec.releaseOutputBuffer(o, false)
                    if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) outputDone = true
                }
            }
        } finally {
            codec.stop()
            codec.release()
            ex.release()
            source.close()
        }
        return acc.result()
    }
}
