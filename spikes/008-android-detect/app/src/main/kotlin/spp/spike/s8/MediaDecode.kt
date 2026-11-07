// SPDX-License-Identifier: GPL-3.0-or-later
// Android decode substrate for the S8 port: framework MediaExtractor + MediaCodec (hardware where available).
// Replaces the two ffmpeg processes of the C# detector:
//   audio: ffmpeg -ss S -t L -map 0:a:0 -af aresample=async=1:first_pts=0 -ac 1 -ar 16000 -f f32le
//   video: ffmpeg -hwaccel auto -ss S -t L -map 0:v:0 -vf scale=192:108:flags=area,format=gray,showinfo -fps_mode passthrough
package spp.spike.s8

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaExtractor
import android.media.MediaFormat
import java.nio.ByteOrder
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin

object MediaDecode {
    class AudioInfo(val samples: FloatArray, val firstPtsUs: Long, val sampleRate: Int, val channels: Int, val codec: String,
                    val encoderDelay: Int?, val gaps: Int)
    class VideoInfo(val features: AudioSync.VideoFeatures, val frames: Int, val firstPtsUs: Long, val codec: String,
                    val width: Int, val height: Int, val decodeMs: Long, val scaleMs: Long)

    private fun track(ex: MediaExtractor, prefix: String): Int {
        for (i in 0 until ex.trackCount) if (ex.getTrackFormat(i).getString(MediaFormat.KEY_MIME)!!.startsWith(prefix)) return i
        error("no $prefix track")
    }

    /** Mono float PCM at 16 kHz, sample 0 = startUs (first_pts=0: a late first packet is padded with silence). */
    fun decodeAudio(path: String, startUs: Long, lenUs: Long): AudioInfo {
        val ex = MediaExtractor()
        ex.setDataSource(path)
        val t = track(ex, "audio/")
        ex.selectTrack(t)
        val fmt = ex.getTrackFormat(t)
        val enc = if (fmt.containsKey(MediaFormat.KEY_ENCODER_DELAY)) fmt.getInteger(MediaFormat.KEY_ENCODER_DELAY) else null
        ex.seekTo(startUs, MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
        val codec = MediaCodec.createDecoderByType(fmt.getString(MediaFormat.KEY_MIME)!!)
        codec.configure(fmt, null, null, 0)
        codec.start()
        var sr = fmt.getInteger(MediaFormat.KEY_SAMPLE_RATE)
        var chans = fmt.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
        val endUs = startUs + lenUs
        var pcm = FloatArray(((lenUs / 1e6) * sr).toInt() + sr)   // grows if needed
        var written = 0                                          // samples placed so far (timeline position)
        var firstPts = Long.MIN_VALUE
        var gaps = 0
        val info = MediaCodec.BufferInfo()
        var inEos = false
        var outEos = false
        while (!outEos) {
            if (!inEos) {
                val ii = codec.dequeueInputBuffer(5000)
                if (ii >= 0) {
                    val buf = codec.getInputBuffer(ii)!!
                    val n = ex.readSampleData(buf, 0)
                    if (n < 0 || ex.sampleTime > endUs) { codec.queueInputBuffer(ii, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM); inEos = true }
                    else { codec.queueInputBuffer(ii, 0, n, ex.sampleTime, 0); ex.advance() }
                }
            }
            val oi = codec.dequeueOutputBuffer(info, 5000)
            if (oi == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                val of = codec.outputFormat
                sr = of.getInteger(MediaFormat.KEY_SAMPLE_RATE); chans = of.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
            } else if (oi >= 0) {
                if (info.size > 0) {
                    val sb = codec.getOutputBuffer(oi)!!.order(ByteOrder.nativeOrder()).asShortBuffer()
                    val frames = info.size / 2 / chans
                    if (firstPts == Long.MIN_VALUE) firstPts = info.presentationTimeUs
                    // aresample async=1: place every buffer at its own timestamp (pad gaps, overwrite small overlaps)
                    val at = Math.round((info.presentationTimeUs - startUs) * sr / 1e6).toInt()
                    if (at > written + 1) gaps++
                    var pos = at
                    if (pcm.size < pos + frames) pcm = pcm.copyOf(max(pcm.size * 2, pos + frames))
                    for (f in 0 until frames) {
                        var s = 0f
                        for (c in 0 until chans) s += sb.get(f * chans + c)
                        if (pos >= 0) pcm[pos] = s / chans / 32768f
                        pos++
                    }
                    written = max(written, pos)
                }
                codec.releaseOutputBuffer(oi, false)
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) outEos = true
            }
        }
        codec.stop(); codec.release(); ex.release()
        val total = min(written, ((lenUs / 1e6) * sr).toInt())
        val mono = pcm.copyOf(max(0, total))
        return AudioInfo(resample(mono, sr, AudioSync.SR), firstPts, sr, chans, codec.name, enc, gaps)
    }

    /** Windowed-sinc (Blackman) resampler, cutoff 0.45 of the lower rate. */
    fun resample(x: FloatArray, from: Int, to: Int): FloatArray {
        if (from == to) return x
        val ratio = to.toDouble() / from
        val n = (x.size * ratio).toInt()
        val fc = 0.45 * min(1.0, ratio)                 // cycles per input sample
        val half = 32
        val phases = 1024
        // kernel table per fractional phase: taps k = c-half+1 .. c+half, d = t - k = frac + half - 1 - j
        val table = Array(phases + 1) { p ->
            val frac = p / phases.toDouble()
            val hk = DoubleArray(2 * half)
            var ws = 0.0
            for (j in 0 until 2 * half) {
                val d = frac + (half - 1 - j)
                val sinc = if (d == 0.0) 2 * fc else sin(2 * PI * fc * d) / (PI * d)
                val wpos = (d + half) / (2.0 * half)
                val win = 0.42 - 0.5 * cos(2 * PI * wpos) + 0.08 * cos(4 * PI * wpos)
                hk[j] = sinc * win; ws += hk[j]
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

    /**
     * Video features from the luma plane, box-downscaled to 192x108 (each source pixel goes to exactly one target cell;
     * ffmpeg's area scaler weights border pixels fractionally - irrelevant for flat test frames, measured on real footage).
     */
    fun decodeVideo(path: String, startUs: Long, lenUs: Long): VideoInfo {
        val ex = MediaExtractor()
        ex.setDataSource(path)
        val t = track(ex, "video/")
        ex.selectTrack(t)
        val fmt = ex.getTrackFormat(t)
        ex.seekTo(startUs, MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
        val codec = MediaCodec.createDecoderByType(fmt.getString(MediaFormat.KEY_MIME)!!)
        fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
        codec.configure(fmt, null, null, 0)
        codec.start()
        val acc = AudioSync.VideoAccumulator()
        val endUs = startUs + lenUs
        val info = MediaCodec.BufferInfo()
        var inEos = false
        var outEos = false
        var firstPts = Long.MIN_VALUE
        var w = 0; var h = 0
        var colIdx = IntArray(0); var rowIdx = IntArray(0); var cnt = IntArray(0)
        val sums = IntArray(AudioSync.W * AudioSync.H)
        var scaleNs = 0L
        val t0 = System.nanoTime()
        var row = ByteArray(0)
        while (!outEos) {
            if (!inEos) {
                val ii = codec.dequeueInputBuffer(5000)
                if (ii >= 0) {
                    val buf = codec.getInputBuffer(ii)!!
                    val n = ex.readSampleData(buf, 0)
                    if (n < 0 || ex.sampleTime > endUs + 200_000) { codec.queueInputBuffer(ii, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM); inEos = true }
                    else { codec.queueInputBuffer(ii, 0, n, ex.sampleTime, 0); ex.advance() }
                }
            }
            val oi = codec.dequeueOutputBuffer(info, 5000)
            if (oi >= 0) {
                val pts = info.presentationTimeUs
                if (info.size > 0 && pts >= startUs && pts < endUs) {
                    val img = codec.getOutputImage(oi)!!
                    val crop = img.cropRect
                    if (w != crop.width() || h != crop.height()) {
                        w = crop.width(); h = crop.height()
                        colIdx = IntArray(w) { min(AudioSync.W - 1, it * AudioSync.W / w) }
                        rowIdx = IntArray(h) { min(AudioSync.H - 1, it * AudioSync.H / h) }
                        cnt = IntArray(AudioSync.W * AudioSync.H)
                        for (y in 0 until h) for (x in 0 until w) cnt[rowIdx[y] * AudioSync.W + colIdx[x]]++
                        row = ByteArray(w)
                    }
                    val s0 = System.nanoTime()
                    val plane = img.planes[0]
                    val yb = plane.buffer
                    val rs = plane.rowStride
                    val ps = plane.pixelStride
                    sums.fill(0)
                    for (y in 0 until h) {
                        val base = (crop.top + y) * rs + crop.left * ps
                        val r = rowIdx[y] * AudioSync.W
                        if (ps == 1) { yb.position(base); yb.get(row, 0, w); for (x in 0 until w) sums[r + colIdx[x]] += row[x].toInt() and 0xFF }
                        else for (x in 0 until w) sums[r + colIdx[x]] += yb.get(base + x * ps).toInt() and 0xFF
                    }
                    val g = ByteArray(AudioSync.W * AudioSync.H) { ((sums[it] + cnt[it] / 2) / cnt[it]).toByte() }
                    scaleNs += System.nanoTime() - s0
                    img.close()
                    if (firstPts == Long.MIN_VALUE) firstPts = pts
                    acc.add(g, (pts - startUs) / 1e6)
                }
                codec.releaseOutputBuffer(oi, false)
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) outEos = true
            }
        }
        val total = (System.nanoTime() - t0) / 1_000_000
        val name = codec.name
        codec.stop(); codec.release(); ex.release()
        return VideoInfo(acc.result(), acc.count, firstPts, name, w, h, total - scaleNs / 1_000_000, scaleNs / 1_000_000)
    }
}
