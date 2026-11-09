// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import android.content.Context
import android.content.pm.ApplicationInfo
import android.util.Log
import android.view.Surface
import dev.jdtech.mpv.MPVLib
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull

/** The player's decoder choice. GPU is `hwdec=mediacodec`; CPU is mpv's software decoder. */
enum class DecodeMode { Gpu, Cpu }

/**
 * One libmpv instance. Options follow the desktop's: no user config, `keep-open`, `hr-seek`, pitch correction; `hwdec=mediacodec` (S2 needs hardware decode for the
 * seek budget). Events arrive on mpv's own thread (no polling).
 */
class MpvEngine(context: Context, decode: DecodeMode) : PlayerEngine {
    private val mpv: MPVLib = requireNotNull(MPVLib.create(context.applicationContext)) { "libmpv could not be created" }
    private val debug = context.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0
    private val listeners = CopyOnWriteArrayList<(Double) -> Unit>()
    private val gate = Any()
    private var pendingLoad: LoadWait? = null
    private var destroyed = false

    @Volatile private var time = Double.NaN

    @Volatile var onPaused: ((Boolean) -> Unit)? = null

    override var onEof: (() -> Unit)? = null

    private class LoadWait {
        val done = CompletableDeferred<Boolean>()
        var started = false
    }

    override val timePos: Double get() = time

    private val observer = object : MPVLib.EventObserver {
        override fun eventProperty(property: String) = Unit
        override fun eventProperty(property: String, value: Long) = Unit
        override fun eventProperty(property: String, value: String) = Unit

        override fun eventProperty(property: String, value: Double) {
            if (property == "time-pos") {
                time = value
                listeners.forEach { it(value) }
            }
        }

        override fun eventProperty(property: String, value: Boolean) {
            when (property) {
                "eof-reached" -> if (value) onEof?.invoke()
                "pause" -> onPaused?.invoke(value)
            }
        }

        override fun event(eventId: Int) {
            when (eventId) {
                MPVLib.MpvEvent.MPV_EVENT_START_FILE -> synchronized(gate) { pendingLoad?.started = true }
                MPVLib.MpvEvent.MPV_EVENT_PLAYBACK_RESTART -> synchronized(gate) { pendingLoad?.takeIf { it.started }?.done?.complete(true) }
                MPVLib.MpvEvent.MPV_EVENT_END_FILE -> synchronized(gate) { pendingLoad?.takeIf { it.started }?.done?.complete(false) }
            }
        }
    }

    init {
        fun opt(name: String, value: String) = mpv.setOptionString(name, value)
        opt("config", "no")
        opt("idle", "yes")
        opt("keep-open", "yes")
        opt("hr-seek", "yes")
        opt("osc", "no")
        opt("osd-level", "0")
        opt("input-default-bindings", "no")
        opt("terminal", "no")
        opt("load-scripts", "no")
        opt("ytdl", "no")
        opt("audio-pitch-correction", "yes")
        opt("pause", "yes")
        opt("vo", "gpu")
        opt("gpu-context", "android")
        opt("opengl-es", "yes")
        opt("ao", "audiotrack,opensles")
        opt("hwdec", if (decode == DecodeMode.Gpu) "mediacodec" else "no")
        opt("hwdec-codecs", "h264,hevc,av1,vp9")
        opt("demuxer-readahead-secs", "20")
        opt("demuxer-max-bytes", DEMUXER_FORWARD_BYTES.toString())
        opt("demuxer-max-back-bytes", DEMUXER_BACK_BYTES.toString())
        mpv.init()
        if (debug) {
            mpv.addLogObserver(object : MPVLib.LogObserver {
                override fun logMessage(prefix: String, level: Int, text: String) {
                    if (level <= LOG_WARN) Log.w(TAG, "$prefix: ${text.trimEnd()}")
                }
            })
        }
        mpv.addObserver(observer)
        mpv.observeProperty("time-pos", MPVLib.MpvFormat.MPV_FORMAT_DOUBLE)
        mpv.observeProperty("eof-reached", MPVLib.MpvFormat.MPV_FORMAT_FLAG)
        mpv.observeProperty("pause", MPVLib.MpvFormat.MPV_FORMAT_FLAG)
    }

    fun attachSurface(surface: Surface) {
        mpv.attachSurface(surface)
        mpv.setOptionString("force-window", "yes")
        mpv.setPropertyString("vo", "gpu")
    }

    fun surfaceSize(width: Int, height: Int) = mpv.setPropertyString("android-surface-size", "${width}x$height")

    fun detachSurface() {
        mpv.setPropertyString("vo", "null")
        mpv.setOptionString("force-window", "no")
        mpv.detachSurface()
    }

    override suspend fun load(edl: String, startSeconds: Double, play: Boolean, endSeconds: Double?): Boolean {
        val wait = LoadWait()
        synchronized(gate) {
            pendingLoad?.done?.complete(false)                                        // superseded
            pendingLoad = wait
        }
        mpv.setPropertyBoolean("pause", !play)                                        // `pause` survives loadfile: always explicit
        var options = "start=" + number(startSeconds)
        if (endSeconds != null && endSeconds.isFinite() && endSeconds > startSeconds) options += ",end=" + number(endSeconds)
        mpv.command(arrayOf("loadfile", "memory://$edl", "replace", "-1", options))
        val ok = withTimeoutOrNull(LOAD_TIMEOUT_MILLIS) { wait.done.await() } ?: false
        synchronized(gate) { if (pendingLoad === wait) pendingLoad = null }
        if (ok) {
            // After an EOF, keep-open's own pause can land after the new file started. The caller's intent wins.
            if (mpv.getPropertyBoolean("pause") != !play) mpv.setPropertyBoolean("pause", !play)
            mpv.getPropertyDouble("time-pos")?.let { time = it }
        }
        return ok
    }

    override fun frameStep() = mpv.command(arrayOf("frame-step"))

    override fun setPause(paused: Boolean) = mpv.setPropertyBoolean("pause", paused)

    override fun addTimeListener(listener: (Double) -> Unit) {
        listeners.add(listener)
    }

    override fun removeTimeListener(listener: (Double) -> Unit) {
        listeners.remove(listener)
    }

    fun setSpeed(speed: Double) = mpv.setPropertyDouble("speed", speed.coerceIn(0.25, 4.0))

    fun setVolume(percent: Double) = mpv.setPropertyDouble("volume", percent.coerceIn(0.0, 130.0))

    fun setMute(muted: Boolean) = mpv.setPropertyBoolean("mute", muted)

    fun setAudioDelay(seconds: Double) = mpv.setPropertyDouble("audio-delay", seconds)

    /** Which decoder mpv is really using, such as `mediacodec`; `no` or null means software. */
    fun hwdecCurrent(): String? = mpv.getPropertyString("hwdec-current")

    /** A number of mpv's own statistics, for measurements. */
    fun stat(name: String): Double? = mpv.getPropertyDouble(name)

    fun destroy() {
        synchronized(gate) {
            if (destroyed) return
            destroyed = true
            pendingLoad?.done?.complete(false)
        }
        listeners.clear()
        mpv.removeObserver(observer)
        mpv.destroy()
    }

    private fun number(v: Double) = String.format(Locale.ROOT, "%.6f", v)

    private companion object {
        const val TAG = "SppMpv"
        const val LOG_WARN = 30
        const val LOAD_TIMEOUT_MILLIS = 20_000L
        const val DEMUXER_FORWARD_BYTES = 48L * 1024 * 1024
        const val DEMUXER_BACK_BYTES = 16L * 1024 * 1024
    }
}
