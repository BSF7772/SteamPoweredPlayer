// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S7: Android export of a downloaded range.
//  A) lossless: MediaExtractor -> Media3 Mp4Muxer, samples in decode order with their original PTS, audio PTS + shift
//     (the muxer writes the offset as an edit list), optional ColorInfo(BT.709, FULL) so an nclx box is written.
//  B) re-encode: Media3 Transformer (MediaCodec HW encoder), optional speed change.
// Driven over adb, results in files/results/<out>.json, log tag SPP-S7:
//   am start -n spp.spike.s7/.MainActivity --es mode remux --es video <f> [--es audio <f>] --es out <f>
//        [--ei shift_ms 300] [--ez color_full true] [--ef start <s>] [--ef len <s>]
//   am start -n spp.spike.s7/.MainActivity --es mode transcode --es in <f> --es out <f> [--ef speed 2] [--es vmime video/hevc]
//        [--ei bitrate 0 (= encoder default)]
//   am start -n spp.spike.s7/.MainActivity --es mode codecs        (MediaCodecList dump: AV1 encoder/decoder check)
// Paths are relative to the app's external files dir.
package spp.spike.s7

import android.app.Activity
import android.media.MediaCodecList
import android.media.MediaExtractor
import android.media.MediaFormat
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.ScrollView
import android.widget.TextView
import androidx.media3.common.C
import androidx.media3.common.ColorInfo
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.audio.SpeedProvider
import androidx.media3.common.util.MediaFormatUtil
import androidx.media3.muxer.BufferInfo
import androidx.media3.muxer.Mp4Muxer
import androidx.media3.transformer.Composition
import androidx.media3.transformer.DefaultEncoderFactory
import androidx.media3.transformer.EditedMediaItem
import androidx.media3.transformer.ExportException
import androidx.media3.transformer.ExportResult
import androidx.media3.transformer.Transformer
import androidx.media3.transformer.VideoEncoderSettings
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.nio.ByteBuffer

@androidx.annotation.OptIn(markerClass = [androidx.media3.common.util.UnstableApi::class])
class MainActivity : Activity() {
    private lateinit var log: TextView
    private lateinit var files: File

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        log = TextView(this).apply { setPadding(24, 24, 24, 24); textSize = 12f }
        setContentView(ScrollView(this).apply { addView(log) })
        files = getExternalFilesDir(null)!!
        say("S7 export spike, ${Build.MANUFACTURER} ${Build.MODEL}, SDK ${Build.VERSION.SDK_INT}")
        when (intent.getStringExtra("mode")) {
            "remux" -> Thread { guard("remux") { remux() } }.start()
            "transcode" -> guard("transcode") { transcode() }      // Transformer needs a Looper thread: main
            "codecs" -> guard("codecs") { finish(codecs(), "codecs") }
            else -> say("pass --es mode remux|transcode|codecs")
        }
    }

    private fun guard(what: String, f: () -> Unit) = try { f() } catch (t: Throwable) {
        say("ERROR $what: $t"); Log.e(TAG, what, t)
        finish(JSONObject().put("mode", what).put("error", t.toString()), "error-$what")
    }

    // ------------------------------------------------------------------ A) lossless remux

    private class Src(val ex: MediaExtractor, val track: Int, val fmt: MediaFormat, var muxTrack: Int = -1)

    private fun open(path: String, prefix: String): Src? {
        val ex = MediaExtractor()
        ex.setDataSource(File(files, path).absolutePath)
        for (i in 0 until ex.trackCount) {
            val f = ex.getTrackFormat(i)
            if (f.getString(MediaFormat.KEY_MIME)!!.startsWith(prefix)) { ex.selectTrack(i); return Src(ex, i, f) }
        }
        ex.release(); return null
    }

    private fun remux() {
        val videoPath = intent.getStringExtra("video")!!
        val audioPath = intent.getStringExtra("audio") ?: videoPath
        val outPath = intent.getStringExtra("out")!!
        val shiftUs = intent.getIntExtra("shift_ms", 300) * 1000L
        val colorFull = intent.getBooleanExtra("color_full", false)
        val startS = intent.getFloatExtra("start", -1f)
        val lenS = intent.getFloatExtra("len", -1f)
        val t0 = System.nanoTime()
        val v = open(videoPath, "video/") ?: error("no video track in $videoPath")
        val a = open(audioPath, "audio/")
        // Absolute (tfdt) timeline: range start defaults to the first video sample.
        val firstV = v.ex.sampleTime
        val startUs = if (startS >= 0) firstV + (startS * 1e6).toLong() else firstV
        val endUs = if (lenS > 0) startUs + (lenS * 1e6).toLong() else Long.MAX_VALUE
        if (startUs > firstV) v.ex.seekTo(startUs, MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
        a?.ex?.seekTo(startUs, MediaExtractor.SEEK_TO_CLOSEST_SYNC)

        val out = File(files, outPath).apply { parentFile?.mkdirs() }
        val fos = FileOutputStream(out)
        val muxer = Mp4Muxer.Builder(fos).build()
        var vf = MediaFormatUtil.createFormatFromMediaFormat(v.fmt)
        val srcColor = vf.colorInfo?.toString() ?: "null"
        if (colorFull) vf = vf.buildUpon().setColorInfo(ColorInfo.Builder()
            .setColorSpace(C.COLOR_SPACE_BT709).setColorRange(C.COLOR_RANGE_FULL).setColorTransfer(C.COLOR_TRANSFER_SDR).build()).build()
        v.muxTrack = muxer.addTrack(vf)
        if (a != null) a.muxTrack = muxer.addTrack(MediaFormatUtil.createFormatFromMediaFormat(a.fmt))

        val buf = ByteBuffer.allocateDirect(4 * 1024 * 1024)
        var vCount = 0; var aCount = 0; var bytes = 0L
        var nonMonotonic = 0; var lastVPts = Long.MIN_VALUE
        var firstVOut = Long.MIN_VALUE; var lastVOut = 0L; var firstAOut = Long.MIN_VALUE
        val srcs = listOfNotNull(v, a)
        // interleave by sample time (decode order inside each track is kept: we only ever take a track's next sample)
        while (true) {
            val s = srcs.filter { it.ex.sampleTime >= 0 && it.ex.sampleTime < endUs + (if (it === v) 0 else 0) }
                .minByOrNull { it.ex.sampleTime + (if (it === a) shiftUs else 0) } ?: break
            buf.clear()
            val n = s.ex.readSampleData(buf, 0)
            if (n < 0) { s.ex.advance(); continue }
            val pts = s.ex.sampleTime
            val sync = s.ex.sampleFlags and MediaExtractor.SAMPLE_FLAG_SYNC != 0
            buf.position(0); buf.limit(n)
            if (s === v) {
                if (vCount == 0 && !sync) { s.ex.advance(); continue }   // start on a key frame
                if (pts < lastVPts) nonMonotonic++
                lastVPts = pts
                if (firstVOut == Long.MIN_VALUE) firstVOut = pts
                lastVOut = maxOf(lastVOut, pts)
                muxer.writeSampleData(v.muxTrack, buf, BufferInfo(pts, n, if (sync) C.BUFFER_FLAG_KEY_FRAME else 0))
                vCount++
            } else {
                if (pts < startUs) { s.ex.advance(); continue }
                val p = pts + shiftUs
                if (firstAOut == Long.MIN_VALUE) firstAOut = p
                muxer.writeSampleData(a!!.muxTrack, buf, BufferInfo(p, n, C.BUFFER_FLAG_KEY_FRAME))
                aCount++
            }
            bytes += n
            s.ex.advance()
        }
        muxer.close(); fos.close()
        srcs.forEach { it.ex.release() }
        val ms = (System.nanoTime() - t0) / 1_000_000
        finish(JSONObject().put("mode", "remux").put("video", videoPath).put("audio", audioPath).put("out", outPath)
            .put("shift_ms", shiftUs / 1000).put("color_full", colorFull).put("src_color_info", srcColor)
            .put("video_mime", v.fmt.getString(MediaFormat.KEY_MIME)).put("video_samples", vCount).put("audio_samples", aCount)
            .put("video_pts_decreases", nonMonotonic).put("first_video_pts_us", firstVOut).put("first_audio_pts_us_shifted", firstAOut)
            .put("media_span_s", (lastVOut - firstVOut) / 1e6).put("bytes", bytes).put("out_size", out.length()).put("wall_ms", ms), outPath)
    }

    // ------------------------------------------------------------------ B) Transformer re-encode

    private fun transcode() {
        val inPath = intent.getStringExtra("in")!!
        val outPath = intent.getStringExtra("out")!!
        val speed = intent.getFloatExtra("speed", 1f)
        val vmime = intent.getStringExtra("vmime") ?: MimeTypes.VIDEO_H265
        val bitrate = intent.getIntExtra("bitrate", 0)
        val inFile = File(files, inPath)
        val out = File(files, outPath).apply { parentFile?.mkdirs(); delete() }
        var b = EditedMediaItem.Builder(MediaItem.fromUri(Uri.fromFile(inFile)))
        if (speed != 1f) b = b.setSpeed(object : SpeedProvider {
            override fun getSpeed(timeUs: Long) = speed
            override fun getNextSpeedChangeTimeUs(timeUs: Long) = C.TIME_UNSET
        })
        val item = b.build()
        var enc = DefaultEncoderFactory.Builder(this).setEnableFallback(true)
        if (bitrate > 0) enc = enc.setRequestedVideoEncoderSettings(VideoEncoderSettings.Builder().setBitrate(bitrate).build())
        val t0 = System.nanoTime()
        val tr = Transformer.Builder(this)
            .setVideoMimeType(vmime)
            .setAudioMimeType(MimeTypes.AUDIO_AAC)
            .setEncoderFactory(enc.build())
            .addListener(object : Transformer.Listener {
                override fun onCompleted(composition: Composition, r: ExportResult) {
                    val ms = (System.nanoTime() - t0) / 1_000_000
                    finish(JSONObject().put("mode", "transcode").put("in", inPath).put("out", outPath).put("speed", speed.toDouble())
                        .put("wall_ms", ms).put("media_out_ms", r.durationMs)
                        .put("realtime_factor_vs_input", if (ms > 0) r.durationMs * speed / ms else 0)
                        .put("video_encoder", r.videoEncoderName).put("video_mime", r.videoMimeType).put("audio_encoder", r.audioEncoderName)
                        .put("avg_video_bitrate", r.averageVideoBitrate).put("frames", r.videoFrameCount).put("size", "${r.width}x${r.height}")
                        .put("color_info", r.colorInfo?.toString() ?: "null").put("file_size", r.fileSizeBytes), outPath)
                }
                override fun onError(composition: Composition, r: ExportResult, e: ExportException) {
                    say("ERROR transcode: ${e.errorCodeName} $e"); Log.e(TAG, "transcode", e)
                    finish(JSONObject().put("mode", "transcode").put("error", "${e.errorCodeName}: $e")
                        .put("wall_ms", (System.nanoTime() - t0) / 1_000_000), "error-transcode")
                }
            })
            .build()
        tr.start(item, out.absolutePath)
        val h = Handler(Looper.getMainLooper())
        val prog = androidx.media3.transformer.ProgressHolder()
        h.post(object : Runnable {
            override fun run() {
                if (tr.getProgress(prog) != Transformer.PROGRESS_STATE_NOT_STARTED) say("progress ${prog.progress} %")
                h.postDelayed(this, 2000)
            }
        })
    }

    // ------------------------------------------------------------------ codec inventory

    private fun codecs(): JSONObject {
        val arr = JSONArray()
        for (ci in MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos) {
            val types = ci.supportedTypes.filter { it.startsWith("video/") }
            if (types.isEmpty()) continue
            arr.put(JSONObject().put("name", ci.name).put("encoder", ci.isEncoder).put("hw", ci.isHardwareAccelerated)
                .put("sw_only", ci.isSoftwareOnly).put("types", JSONArray(types)))
        }
        val list = MediaCodecList(MediaCodecList.REGULAR_CODECS)
        val av1 = MediaFormat.createVideoFormat(MimeTypes.VIDEO_AV1, 1920, 1080)
        return JSONObject().put("mode", "codecs").put("codecs", arr)
            .put("av1_encoder", list.findEncoderForFormat(av1) ?: JSONObject.NULL)
            .put("av1_decoder", list.findDecoderForFormat(av1) ?: JSONObject.NULL)
    }

    private fun finish(o: JSONObject, name: String) {
        o.put("device", "${Build.MANUFACTURER} ${Build.MODEL} SDK ${Build.VERSION.SDK_INT}")
        val dir = File(files, "results").apply { mkdirs() }
        val f = File(dir, name.replace('/', '_') + ".json")
        f.writeText(o.toString(2))
        say("RESULT $o")
        say("DONE results/${f.name}")
    }

    private fun say(s: String) {
        Log.i(TAG, s)
        runOnUiThread { log.append(s + "\n") }
    }

    companion object { const val TAG = "SPP-S7" }
}
