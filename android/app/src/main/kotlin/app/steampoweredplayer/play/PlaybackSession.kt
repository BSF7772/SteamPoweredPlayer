// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import app.steampoweredplayer.data.SessionMedia
import java.io.IOException
import kotlin.math.abs
import kotlin.math.max
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/** What [PlaybackSession] needs from mpv; a fake stands in for it in unit tests. */
interface PlayerEngine {
    /** mpv's `time-pos`; NaN with nothing on screen. */
    val timePos: Double

    /** Replaces what's loaded. True once a first frame is there; false on a timeout or a load that failed. */
    suspend fun load(edl: String, startSeconds: Double, play: Boolean, endSeconds: Double?): Boolean
    fun frameStep()
    fun setPause(paused: Boolean)

    /** Called on the engine's own thread with each new `time-pos`. */
    fun addTimeListener(listener: (Double) -> Unit)
    fun removeTimeListener(listener: (Double) -> Unit)

    /** Called on the engine's own thread when `eof-reached` turns true. */
    var onEof: (() -> Unit)?
}

/** Why a reload happened. */
enum class ReloadCause { Open, Seek, Policy, EndOfWindow, Loop }

/**
 * One recording on one [PlayerEngine], as in the desktop's `PlaybackSession`: window EDLs, the seek-by-reload rule, coalesced seeks and the periodic reload.
 * Every seek and back-step is `loadfile start=`, never an mpv seek: a revisited chunk breaks decode (S2). While a reload runs, new seeks only replace the
 * pending target. `pause` is the user's intent kept here, not read from mpv: `keep-open` pauses mpv at EOF and mpv keeps `pause` across loads.
 *
 * Frame back: mpv can't step back across a chunk and Steam's timestamps are irregular, so the previous frame's real time comes from stepping forward from a few
 * frames earlier. Repeated presses keep moving across chunk boundaries: a slow probe costs that one press its exactness and never turns exact steps off, nothing
 * here pauses an engine that's already paused (that cancels a step in flight), and a load that lands on the frame it left steps further.
 */
class PlaybackSession(
    private val engine: PlayerEngine,
    private val scope: CoroutineScope,
    private val url: (String) -> String = { it },
    private val windowCount: Int = EdlWindow.DEFAULT_COUNT,
    private val frameProbeTimeoutMillis: Long = 500,
) {
    private data class Pending(
        val position: Double,
        val cause: ReloadCause,
        val force: Boolean,
        val play: Boolean?,
        val back: Int = 0,
        val origin: Double = 0.0,
        val frame: Double = 0.0,
    )

    private val gate = Any()
    private var media: SessionMedia? = null
    private var window: List<Int> = emptyList()
    private var edl = ""
    private var userPaused = false
    private var loopA: Double? = null
    private var loopB: Double? = null
    private var loopEngaged = false
    private var position = Double.NaN
    private var lastTick = 0.0
    private var playedSinceLoad = 0.0
    private var reloading = false
    private var atEnd = false
    private var closed = false
    private var pending: Pending? = null
    private var pumping = false
    private var waiters = ArrayList<CompletableDeferred<Unit>>()
    private var frames: List<Double>? = null

    /** Position moved during sequential play; not while a reload is pending. On the engine's thread. */
    var onPosition: ((Double) -> Unit)? = null

    /** The engine reached the end of what's loaded. On the engine's thread. */
    var onReachedEnd: ((Double) -> Unit)? = null

    var onReloaded: ((ReloadCause, Double) -> Unit)? = null
    var onReloadFailed: ((Exception) -> Unit)? = null

    private val timeListener: (Double) -> Unit = ::onTimePos

    init {
        engine.addTimeListener(timeListener)
        engine.onEof = ::onEof
    }

    val currentMedia: SessionMedia? get() = synchronized(gate) { media }
    val currentWindow: List<Int> get() = synchronized(gate) { window }
    val isPaused: Boolean get() = synchronized(gate) { userPaused }
    val isReloading: Boolean get() = synchronized(gate) { reloading }
    val reachedEnd: Boolean get() = synchronized(gate) { atEnd }

    /** Media time; while a reload is pending, the snapped target. */
    val currentPosition: Double get() = synchronized(gate) { position }
    val isLoopEngaged: Boolean get() = synchronized(gate) { loopEngaged }

    fun open(newMedia: SessionMedia, start: Double? = null, play: Boolean = true): CompletableDeferred<Unit> {
        synchronized(gate) {
            media = newMedia
            window = emptyList()
            userPaused = !play
            frames = null
        }
        return enqueue(Pending(start ?: newMedia.startSeconds, ReloadCause.Open, true, play))
    }

    fun seek(mediaSeconds: Double): CompletableDeferred<Unit> = enqueue(Pending(mediaSeconds, ReloadCause.Seek, false, null))

    fun seekRelative(delta: Double): CompletableDeferred<Unit> = seek(currentPosition + delta)

    /** One frame back. Rapid calls add up: three calls move three frames. [frameSeconds] (1/60) is only where to look, and the step when nothing better is known. */
    fun stepBack(frameSeconds: Double = DEFAULT_FRAME_SECONDS): CompletableDeferred<Unit> {
        require(frameSeconds > 0 && frameSeconds.isFinite())
        return enqueue(Pending(Double.NaN, ReloadCause.Seek, false, null, back = 1, origin = Double.NaN, frame = frameSeconds))
    }

    /** One frame forward. */
    fun frameStep() {
        synchronized(gate) { userPaused = true }
        engine.frameStep()
    }

    fun setPause(paused: Boolean) {
        val changed = synchronized(gate) {
            val was = userPaused
            userPaused = paused
            was != paused
        }
        if (changed) engine.setPause(paused)
    }

    /** The A-B loop, driven here: at B it reloads at A, because mpv's own ab-loop revisits a chunk and wraps early (P1). Null or B ≤ A clears it. */
    fun setLoop(a: Double?, b: Double?) = synchronized(gate) {
        if (a != null && b != null && a.isFinite() && b.isFinite() && b > a) {
            loopA = a
            loopB = b
            loopEngaged = true
        } else {
            loopA = null
            loopB = null
            loopEngaged = false
        }
    }

    /** Swaps in a new set of chunks without reloading: more got downloaded. */
    fun replaceMedia(newMedia: SessionMedia) = synchronized(gate) {
        media = newMedia
        frames = null
    }

    fun reload(mediaSeconds: Double, cause: ReloadCause, force: Boolean = true) = enqueue(Pending(mediaSeconds, cause, force, null))

    fun close() {
        synchronized(gate) {
            closed = true
            pending = null
        }
        engine.removeTimeListener(timeListener)
        engine.onEof = null
    }

    private fun enqueue(request: Pending): CompletableDeferred<Unit> {
        val done = CompletableDeferred<Unit>()
        synchronized(gate) {
            if (closed) {
                done.completeExceptionally(IOException("closed"))
                return done
            }
            val m = media
            if (m == null || m.chunkCount == 0) {
                done.completeExceptionally(IOException("No media is open."))
                return done
            }
            var req = request
            if (req.back > 0) {
                val p = pending
                req = when {
                    p != null && p.back > 0 -> req.copy(back = p.back + req.back, origin = p.origin)      // back-steps add up against the burst's origin
                    p != null -> Pending(p.position - req.frame, ReloadCause.Seek, false, null)
                    else -> req.copy(origin = position)
                }
                if (req.back > 0) req = req.copy(position = req.origin - req.back * req.frame)
            }
            pending = if (pending?.force == true && !req.force) req.copy(force = true) else req        // latest wins; a forced rebuild isn't lost
            waiters.add(done)
            reloading = true
            position = snap(m, req.position)
            val a = loopA
            val b = loopB
            if (req.cause == ReloadCause.Seek && a != null && b != null) loopEngaged = position >= a - LOOP_EDGE_SECONDS && position < b
            atEnd = false
            if (!pumping) {
                pumping = true
                scope.launch { pumpLoop() }
            }
        }
        return done
    }

    private suspend fun pumpLoop() {
        while (true) {
            val request: Pending
            val released: List<CompletableDeferred<Unit>>
            synchronized(gate) {
                val p = pending
                if (p == null || closed) {
                    pumping = false
                    reloading = false
                    val rest = waiters
                    waiters = ArrayList()
                    rest.forEach { it.completeExceptionally(IOException("closed")) }
                    return
                }
                request = p
                pending = null
                released = waiters
                waiters = ArrayList()
            }
            var error: Exception? = null
            try {
                reload(request)
            } catch (e: IOException) {
                error = e
            }
            error?.let { onReloadFailed?.invoke(it) }
            val finished = synchronized(gate) {
                val last = pending == null
                if (last) {
                    pumping = false                                                   // before the waiters run: their next request starts a new pump
                    reloading = false
                }
                last
            }
            released.forEach { w -> error?.let(w::completeExceptionally) ?: w.complete(Unit) }
            if (finished) return
        }
    }

    private suspend fun reload(requested: Pending) {
        var req = requested
        var shown: Double? = null
        if (req.back > 0) {
            // The presses count from what's on screen now, the result of the reload before this one, whatever time was expected when they came.
            val onScreen = engine.timePos
            if (onScreen.isFinite()) req = req.copy(origin = onScreen, position = onScreen - req.back * req.frame)
            val exact = exactBack(req)
            if (exact != null) {
                shown = exact.second
                req = req.copy(position = exact.first, play = false)
            } else {
                req = req.copy(position = req.origin - req.back * req.frame, play = false)
            }
            synchronized(gate) { userPaused = true }                                  // like the forward step, a frame step is a paused operation
        }
        var target: Double
        val play: Boolean
        val end: Double?
        val script: String
        synchronized(gate) {
            val m = media ?: throw IOException("No media is open.")
            end = endLimitOf(m)
            target = snap(m, req.position)
            play = req.play ?: !userPaused
            ensureWindow(target, req.force)
            script = edl
            if (pending == null) position = if (shown != null && target == req.position) shown else target   // a queued request owns the position until it runs
        }
        if (!engine.load(script, target, play, end)) throw IOException("mpv did not deliver a first frame for the reload.")

        if (req.back > 0 && shown == null) target = stepPastSameFrame(req, target, end)

        synchronized(gate) {
            lastTick = if (shown != null && target == req.position) shown else target
            playedSinceLoad = 0.0
            if (pending == null) position = lastTick
        }
        onReloaded?.invoke(req.cause, target)
    }

    /**
     * A nominal back-step can land on the frame it left when a chunk's first frame is more than a frame off the grid. Until the picture really moved back, look
     * further; at the head of the recording there's nothing further to look at.
     */
    private suspend fun stepPastSameFrame(req: Pending, firstTarget: Double, end: Double?): Double {
        var target = firstTarget
        val start = synchronized(gate) { media?.startSeconds ?: 0.0 }
        repeat(MAX_FURTHER_LOADS) {
            val now = engine.timePos
            if (!now.isFinite() || now < req.origin - req.frame * 0.25 || target <= start) return target
            target = max(start, target - req.frame * (it + 2))
            val script = synchronized(gate) {
                ensureWindow(target, false)
                edl
            }
            if (!engine.load(script, target, false, end)) return target
        }
        return target
    }

    private fun ensureWindow(target: Double, force: Boolean) {
        val m = media!!
        val rebuild = force || window.isEmpty() || EdlWindow.shouldReload(window, m, target, 0.0) != ReloadReason.None
        if (rebuild) {
            window = EdlWindow.window(m, target, windowCount)
            edl = Edl.build(m, window, url)
        }
    }

    /** (where to load, the frame's real time) for the frame [req].back frames before the one on screen; null when that isn't known this time. */
    private suspend fun exactBack(req: Pending): Pair<Double, Double>? {
        val m: SessionMedia
        var known: List<Double>?
        synchronized(gate) {
            m = media ?: return null
            known = frames
        }
        val t = engine.timePos
        val frame = req.frame
        if (!t.isFinite()) return null
        val tolerance = 0.25 * frame
        var at = known?.let { nearest(it, t, tolerance) } ?: -1
        if (at < req.back) {
            var margin = PROBE_MARGIN_FRAMES
            while (true) {
                val wanted = t - (req.back + margin) * frame
                val atHead = wanted <= m.startSeconds
                val from = snap(m, wanted)
                val script = synchronized(gate) {
                    ensureWindow(from, false)
                    edl
                }
                if (!engine.load(script, from, false, endLimitOf(m))) return null
                val first = engine.timePos
                if (!first.isFinite()) return null
                val list = arrayListOf(first)
                val cap = req.back + margin + 24
                while (list.last() < t - tolerance && list.size < cap) list.add(step(list.last()) ?: return null)
                at = nearest(list, t, tolerance)
                if (at >= req.back || atHead && at >= 0) {
                    known = list
                    synchronized(gate) { frames = list }
                    break
                }
                if (atHead || margin >= MAX_PROBE_MARGIN_FRAMES) return null
                margin *= 2                                                           // a chunk's first frame can lie further off the grid than the margin
            }
        }
        val list = known ?: return null
        val k = max(0, at - req.back)
        if (k >= list.size) return null
        val gap = if (k > 0) list[k] - list[k - 1] else frame
        return (list[k] - 0.25 * gap) to list[k]                                      // mpv shows the first frame at or after the start
    }

    private suspend fun step(last: Double): Double? {
        val next = CompletableDeferred<Double>()
        val listener: (Double) -> Unit = { v -> if (v.isFinite() && v > last + 1e-6) next.complete(v) }
        engine.addTimeListener(listener)
        return try {
            engine.frameStep()
            withTimeoutOrNull(frameProbeTimeoutMillis) { next.await() }
        } finally {
            engine.removeTimeListener(listener)
        }
    }

    private fun onTimePos(t: Double) {
        if (t.isNaN()) return
        var auto: Pending? = null
        val callback: ((Double) -> Unit)?
        synchronized(gate) {
            val m = media
            if (reloading || closed || m == null) return
            val d = t - lastTick
            lastTick = t
            if (d > 0 && d < 2) playedSinceLoad += d                                 // sequential play; a jump isn't "played"
            position = t
            val reason = EdlWindow.shouldReload(window, m, t, playedSinceLoad)
            val a = loopA
            val b = loopB
            if (loopEngaged && a != null && b != null && t >= b) {
                auto = Pending(a, ReloadCause.Loop, false, null)
            } else if (reason != ReloadReason.None && worthwhile(reason, m, t)) {
                auto = Pending(t, ReloadCause.Policy, true, null)
            }
            callback = onPosition
        }
        callback?.invoke(t)
        auto?.let(::enqueue)
    }

    /** Near a window's end only a window reaching well past the loaded one is worth a reload. */
    private fun worthwhile(reason: ReloadReason, m: SessionMedia, t: Double): Boolean {
        if (reason != ReloadReason.NearWindowEnd) return true
        val candidate = EdlWindow.window(m, t, windowCount)
        return candidate.isNotEmpty() && window.isNotEmpty() && candidate.last() - window.last() >= EdlWindow.RELOAD_MARGIN_CHUNKS
    }

    private fun onEof() {
        var pos = 0.0
        var lap: Double? = null
        synchronized(gate) {
            if (reloading || closed) return
            val a = loopA
            if (loopEngaged && a != null && loopB != null) {
                lap = a                                                               // the end came before B: still a lap
            } else {
                atEnd = true
                val t = engine.timePos
                pos = if (t.isNaN()) position else t
                position = pos
            }
        }
        val a = lap
        if (a != null) enqueue(Pending(a, ReloadCause.Loop, false, null)) else onReachedEnd?.invoke(pos)
    }

    companion object {
        const val DEFAULT_FRAME_SECONDS = 1.0 / 60
        const val END_MARGIN_SECONDS = 0.05
        const val GAP_TAIL_MARGIN_SECONDS = 0.3
        private const val PROBE_MARGIN_FRAMES = 12
        private const val MAX_PROBE_MARGIN_FRAMES = 48
        private const val MAX_FURTHER_LOADS = 4
        private const val LOOP_EDGE_SECONDS = 0.02

        /** Before the head → the head; a gap → the next run; past the end → just before it; the last frames before a gap aren't seekable. NaN counts as the head. */
        fun snap(media: SessionMedia, seconds: Double): Double {
            if (media.chunkCount == 0) return 0.0
            val start = media.startSeconds
            val end = media.endSeconds
            if (seconds.isNaN() || seconds <= start) return start
            if (seconds >= end) return max(start, end - END_MARGIN_SECONDS)
            for (i in 1 until media.runs.size) {
                val gapStart = media.chunkEndSeconds(media.runs[i - 1].last)
                val gapEnd = media.chunkStartSeconds(media.runs[i].first)
                if (seconds >= gapStart - GAP_TAIL_MARGIN_SECONDS && seconds < gapStart) return gapStart - GAP_TAIL_MARGIN_SECONDS
                if (seconds >= gapStart && seconds < gapEnd) return gapEnd
            }
            return seconds
        }

        /** The end to give mpv: only a saved clip's intended end. */
        private fun endLimitOf(m: SessionMedia): Double? = if (m.endLimitSeconds != null) m.endSeconds else null

        private fun nearest(frames: List<Double>, t: Double, tolerance: Double): Int {
            var best = -1
            var bestD = Double.MAX_VALUE
            frames.forEachIndexed { j, f ->
                val d = abs(f - t)
                if (d < bestD) {
                    bestD = d
                    best = j
                }
            }
            return if (bestD <= tolerance) best else -1
        }
    }
}
