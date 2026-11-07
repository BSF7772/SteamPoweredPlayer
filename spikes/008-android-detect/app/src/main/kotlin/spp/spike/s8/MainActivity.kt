// SPDX-License-Identifier: GPL-3.0-or-later
package spp.spike.s8

import android.app.Activity
import android.media.MediaExtractor
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.widget.ScrollView
import android.widget.TextView
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Callable
import java.util.concurrent.Executors
import kotlin.math.max

/**
 * Headless-ish driver, started over adb:
 *   am start -n spp.spike.s8/.MainActivity --es file <name in files/> [--ef start 0] [--ef end 75] [--ei repeat 3]
 *   am start -n spp.spike.s8/.MainActivity --es raw <dir in files/ with audio.f32, video.gray, video.pts>
 * Writes files/results/<name>.json and logs under tag SPP-S8.
 */
class MainActivity : Activity() {
    private lateinit var log: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        log = TextView(this).apply { setPadding(24, 24, 24, 24); textSize = 12f }
        setContentView(ScrollView(this).apply { addView(log) })
        val files = getExternalFilesDir(null)!!
        val file = intent.getStringExtra("file")
        val raw = intent.getStringExtra("raw")
        val start = intent.getFloatExtra("start", 0f).toDouble()
        val end = intent.getFloatExtra("end", -1f).toDouble()
        val repeat = intent.getIntExtra("repeat", 1)
        say("S8 AudioSync port, ${Build.MODEL}, SDK ${Build.VERSION.SDK_INT}; files dir ${files.absolutePath}")
        if (file == null && raw == null) { say("nothing to do: pass --es file <name> or --es raw <dir>"); return }
        Thread {
            try {
                val out = if (raw != null) runRaw(File(files, raw), repeat) else runMedia(File(files, file!!), start, end, repeat)
                val dir = File(files, "results").apply { mkdirs() }
                val name = (raw ?: file!!).replace('/', '_') + ".json"
                File(dir, name).writeText(out.toString(2))
                say("RESULT ${out}")
                say("DONE results/$name")
            } catch (t: Throwable) {
                say("ERROR ${t}")
                Log.e(TAG, "failed", t)
            }
        }.start()
    }

    private fun runRaw(dir: File, repeat: Int): JSONObject {
        val audio = RawInputs.audio(File(dir, "audio.f32"))
        val video = RawInputs.video(File(dir, "video.gray"), File(dir, "video.pts"))
        val o = JSONObject().put("mode", "raw").put("dir", dir.name).put("audio_samples", audio.size).put("video_features", video.pts.size)
        val times = ArrayList<Long>()
        var r: AudioSync.Result? = null
        repeat(max(1, repeat)) {
            val t0 = System.nanoTime()
            r = AudioSync.analyse(audio, video)
            times.add((System.nanoTime() - t0) / 1_000_000)
        }
        return o.put("result", res(r!!)).put("analyse_ms", times.toString())
    }

    private fun runMedia(f: File, start0: Double, end0: Double, repeat: Int): JSONObject {
        var start = start0
        var end = end0
        if (end < 0) {
            val ex = MediaExtractor(); ex.setDataSource(f.absolutePath)
            var d = 0L
            for (i in 0 until ex.trackCount) { val tf = ex.getTrackFormat(i); if (tf.containsKey(MediaFormat.KEY_DURATION)) d = max(d, tf.getLong(MediaFormat.KEY_DURATION)) }
            ex.release(); end = d / 1e6
        }
        // DetectAsync: analyse at most 75 s from the middle of [start, end]
        var len = max(1.0, end - start)
        if (len > 75) { start += (len - 75) / 2; len = 75.0 }
        val o = JSONObject().put("mode", "media").put("file", f.name).put("start", start).put("len", len)
        val runs = org.json.JSONArray()
        val pool = Executors.newFixedThreadPool(2)
        repeat(max(1, repeat)) { rep ->
            val t0 = System.nanoTime()
            val sUs = (start * 1e6).toLong()
            val lUs = (len * 1e6).toLong()
            val af = pool.submit(Callable { val a0 = System.nanoTime(); MediaDecode.decodeAudio(f.absolutePath, sUs, lUs) to (System.nanoTime() - a0) / 1_000_000 })
            val vf = pool.submit(Callable { MediaDecode.decodeVideo(f.absolutePath, sUs, lUs) })
            val (a, aMs) = af.get()
            val v = vf.get()
            val t1 = System.nanoTime()
            val r = AudioSync.analyse(a.samples, v.features)
            val t2 = System.nanoTime()
            runs.put(JSONObject()
                .put("rep", rep)
                .put("result", res(r))
                .put("total_ms", (t2 - t0) / 1_000_000)
                .put("decode_wall_ms", (t1 - t0) / 1_000_000)
                .put("analyse_ms", (t2 - t1) / 1_000_000)
                .put("audio", JSONObject().put("codec", a.codec).put("decode_ms", aMs).put("sr", a.sampleRate).put("ch", a.channels)
                    .put("first_pts_us", a.firstPtsUs).put("encoder_delay", a.encoderDelay ?: JSONObject.NULL).put("gaps", a.gaps)
                    .put("samples16k", a.samples.size))
                .put("video", JSONObject().put("codec", v.codec).put("frames", v.frames).put("size", "${v.width}x${v.height}")
                    .put("first_pts_us", v.firstPtsUs).put("decode_ms", v.decodeMs).put("scale_ms", v.scaleMs)))
            say("rep $rep: ${res(r)} total ${(t2 - t0) / 1_000_000} ms")
        }
        pool.shutdown()
        return o.put("device", "${Build.MANUFACTURER} ${Build.MODEL} SDK ${Build.VERSION.SDK_INT}").put("runs", runs)
    }

    private fun res(r: AudioSync.Result) = JSONObject().put("audio_late_ms", r.audioLate * 1000).put("confidence", r.confidence.name)
        .put("agreeing", r.agreeing).put("consistent", r.consistent).put("in_sync", AudioSync.inSync(r.audioLate))

    private fun say(s: String) {
        Log.i(TAG, s)
        runOnUiThread { log.append(s + "\n") }
    }

    companion object { const val TAG = "SPP-S8" }
}
