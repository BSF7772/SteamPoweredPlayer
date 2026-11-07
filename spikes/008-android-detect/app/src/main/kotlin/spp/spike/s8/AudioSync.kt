// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S8: line-by-line Kotlin port of FastEncoder's src/Core/AudioSync.cs (analysis part).
// Pure Kotlin, no Android types, so it runs both in JVM unit tests and on the phone.
// Numeric choices that must match C#: Math.Round is banker's rounding (Math.rint here),
// float*float stays float before widening (x * x * window[i]), radix-2 FFT with the same twiddle recurrence.
package spp.spike.s8

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.hypot
import kotlin.math.ln
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.sin
import kotlin.math.sqrt

object AudioSync {
    enum class Confidence { None, Low, High }

    /** audioLate = seconds the sound arrives after the picture (negative = early). Fix = delay audio by -audioLate. */
    data class Result(val audioLate: Double, val confidence: Confidence, val agreeing: Int, val consistent: Int)

    fun inSync(audioLate: Double) = audioLate > -0.045 && audioLate < 0.080

    const val R = 200
    const val SR = 16000
    const val WIN = 512
    const val W = 192
    const val H = 108
    const val G = 8
    private const val MAX_LAG = 1.5
    private val SHIFTS = doubleArrayOf(0.0, 0.2, -0.3, 0.5)

    /** pts[i] belongs to values[*][i]; values = motion, flash, cellChg, lumUp. */
    class VideoFeatures(val pts: DoubleArray, val values: Array<DoubleArray>)

    fun analyse(audio: FloatArray, video: VideoFeatures): Result {
        val afeat = audioFeatures(audio)
        val n = afeat[0].size
        if (n < R * 3 || video.pts.size < 30) return Result(0.0, Confidence.None, 0, 0)

        val vgrid = video.values.map { prep(toGrid(video.pts, it, n)) }
        val maxLag = (MAX_LAG * R).toInt()
        // Prep(Shift(af, s)) does not depend on the video feature: compute it once per (feature, shift).
        val aprep = afeat.map { af -> Array(SHIFTS.size) { k -> prep(shift(af, Math.rint(SHIFTS[k] * R).toInt())) } }

        val items = ArrayList<Pair<Double, Double>>()   // (lag, ratio)
        for (ap in aprep)
            for (vg in vgrid) {
                val lags = DoubleArray(SHIFTS.size)
                var ratioSum = 0.0
                for (k in SHIFTS.indices) {
                    val (lag, ratio) = xcorr(ap[k], vg, maxLag)
                    lags[k] = lag / R.toDouble() - SHIFTS[k]
                    ratioSum += ratio
                }
                if (lags.max() - lags.min() <= 0.020) items.add(median(lags) to ratioSum / SHIFTS.size)
            }

        if (items.isEmpty()) return Result(0.0, Confidence.None, 0, 0)

        var best: List<Pair<Double, Double>> = emptyList()
        var bestScore = -Double.MAX_VALUE
        for (it in items) {
            val grp = items.filter { o -> abs(o.first - it.first) <= 0.040 }
            val score = grp.sumOf { g -> g.second - 1 }
            if (score > bestScore) { bestScore = score; best = grp }
        }
        val wsum = best.sumOf { g -> g.second - 0.99 }
        val late = best.sumOf { g -> g.first * (g.second - 0.99) } / wsum
        val conf = if (best.size >= 3 && best.size * 2 > items.size && best.maxOf { it.second } >= 1.3) Confidence.High else Confidence.Low
        return Result(late, conf, best.size, items.size)
    }

    /** Spectral flux, high-band flux (>2 kHz) and loudness rise, at R Hz. Input: mono float at SR. */
    fun audioFeatures(samples: FloatArray): Array<DoubleArray> {
        val a = FloatArray(samples.size + WIN / 2)
        System.arraycopy(samples, 0, a, WIN / 2, samples.size)
        val hop = SR / R
        val n = max(0, (a.size - WIN) / hop)
        val flux = DoubleArray(n)
        val hf = DoubleArray(n)
        val energy = DoubleArray(n)
        val window = DoubleArray(WIN) { i -> 0.5 - 0.5 * cos(2 * PI * i / (WIN - 1)) }

        val bins = WIN / 2 + 1
        val hfBin = (2000 / (SR.toDouble() / WIN)).toInt()
        val prev = DoubleArray(bins)
        val re = DoubleArray(WIN)
        val im = DoubleArray(WIN)
        var prevLog = 0.0
        for (f in 0 until n) {
            val o = f * hop
            var sq = 0.0
            for (i in 0 until WIN) {
                val x = a[o + i]
                sq += (x * x) * window[i]
                re[i] = x * window[i]
                im[i] = 0.0
            }
            fft(re, im)
            var fl = 0.0
            var fh = 0.0
            for (b in 0 until bins) {
                val m = ln(1 + 100 * magnitude(re[b], im[b]))
                val d = if (f == 0) 0.0 else max(0.0, m - prev[b])
                fl += d
                if (b >= hfBin) fh += d
                prev[b] = m
            }
            val lg = ln(sqrt(sq / (WIN / 2.0) + 1e-10))
            energy[f] = if (f == 0) 0.0 else max(0.0, lg - prevLog)
            prevLog = lg
            flux[f] = fl
            hf[f] = fh
        }
        return arrayOf(flux, hf, energy)
    }

    fun toGrid(pts: DoubleArray, vals: DoubleArray, n: Int): DoubleArray {
        val g = DoubleArray(n)
        for (i in vals.indices) {
            val k = Math.rint(pts[i] * R).toInt()
            if (k in 0 until n) g[k] = max(g[k], vals[i])
        }
        return g
    }

    fun shift(x: DoubleArray, s: Int): DoubleArray {
        if (s == 0) return x
        val y = DoubleArray(x.size)
        if (s > 0) System.arraycopy(x, 0, y, s, max(0, x.size - s))
        else System.arraycopy(x, -s, y, 0, max(0, x.size + s))
        return y
    }

    fun prep(x: DoubleArray): DoubleArray {
        val n = x.size
        val pos = x.filter { it > 0 }.toDoubleArray()
        val med = if (pos.isNotEmpty()) median(pos) else 1.0
        val y = DoubleArray(n) { i -> ln(1 + x[i] / (med + 1e-9)) }

        val k = DoubleArray(25) { i -> exp(-0.5 * ((i - 12) / 4.0).pow(2)) }
        val ks = k.sum()
        val b = DoubleArray(n)
        for (i in 0 until n) {
            var s = 0.0
            for (j in 0 until 25) { val t = i + j - 12; if (t in 0 until n) s += y[t] * k[j] }
            b[i] = s / ks
        }

        val w = R / 2
        val cum = DoubleArray(n + 1)
        for (i in 0 until n) cum[i + 1] = cum[i] + b[i]
        var mean = 0.0
        for (i in 0 until n) {
            val lo = i - w / 2
            val hi = lo + w
            val s = cum[hi.coerceIn(0, n)] - cum[lo.coerceIn(0, n)]
            y[i] = b[i] - s / w
            mean += y[i]
        }
        mean /= n
        var v = 0.0
        for (i in 0 until n) v += (y[i] - mean) * (y[i] - mean)
        val sd = sqrt(v / n) + 1e-9
        for (i in 0 until n) y[i] = (y[i] - mean) / sd
        return y
    }

    /** Best lag (positive = audio after picture) and how much it beats the next peak >= 60 ms away. */
    fun xcorr(a: DoubleArray, v: DoubleArray, maxLag: Int): Pair<Int, Double> {
        val m = min(a.size, v.size)
        val cc = DoubleArray(2 * maxLag + 1)
        for (l in -maxLag..maxLag) {
            val i0 = max(0, -l)
            val i1 = m - max(0, l)
            var s = 0.0
            for (i in i0 until i1) s += a[i + l] * v[i]
            cc[l + maxLag] = s / max(1, m - abs(l))
        }
        var best = 0
        for (i in 1 until cc.size) if (cc[i] > cc[best]) best = i
        var second = -Double.MAX_VALUE
        for (i in cc.indices) if (abs(i - best) > 0.06 * R && cc[i] > second) second = cc[i]
        return (best - maxLag) to cc[best] / max(second, 1e-9)
    }

    fun median(x: DoubleArray): Double {
        val s = x.sortedArray()
        return if (s.isEmpty()) 0.0 else if (s.size % 2 == 1) s[s.size / 2] else (s[s.size / 2 - 1] + s[s.size / 2]) / 2
    }

    /** System.Numerics.Complex.Abs (.NET): scaled hypot, reproduced so the features match bit for bit. */
    private fun magnitude(re: Double, im: Double): Double {
        val a = abs(re)
        val b = abs(im)
        val c = max(a, b)
        val d = min(a, b)
        if (d == 0.0) return c
        val q = d / c
        return c * sqrt(1.0 + q * q)
    }

    private fun fft(re: DoubleArray, im: DoubleArray) {
        val n = re.size
        var j = 0
        for (i in 1 until n) {
            var bit = n shr 1
            while (j and bit != 0) { j = j xor bit; bit = bit shr 1 }
            j = j xor bit
            if (i < j) {
                var t = re[i]; re[i] = re[j]; re[j] = t
                t = im[i]; im[i] = im[j]; im[j] = t
            }
        }
        var len = 2
        while (len <= n) {
            val ang = -2 * PI / len
            val wr = cos(ang)
            val wi = sin(ang)
            var i = 0
            while (i < n) {
                var cr = 1.0
                var ci = 0.0
                for (k in 0 until len / 2) {
                    val p = i + k
                    val q = p + len / 2
                    val tr = re[q] * cr - im[q] * ci
                    val ti = re[q] * ci + im[q] * cr
                    re[q] = re[p] - tr; im[q] = im[p] - ti
                    re[p] += tr; im[p] += ti
                    val nr = cr * wr - ci * wi
                    ci = cr * wi + ci * wr
                    cr = nr
                }
                i += len
            }
            len = len shl 1
        }
    }

    /**
     * Per-frame video change features from 192x108 grey frames (DecodeVideoAsync's loop).
     * Feed frames in decode-output (presentation) order with their timestamps in seconds from the range start.
     */
    class VideoAccumulator {
        private val cells = G * G
        private val ch = H / G
        private val cw = W / G
        private var prev = ByteArray(W * H)
        private val prevCells = DoubleArray(cells)
        private val curCells = DoubleArray(cells)
        private val diffs = DoubleArray(cells)
        private val absd = DoubleArray(cells)
        private val motion = ArrayList<Double>()
        private val flash = ArrayList<Double>()
        private val cellChg = ArrayList<Double>()
        private val lumUp = ArrayList<Double>()
        private val pts = ArrayList<Double>()
        private var prevLum = 0.0
        var count = 0
            private set

        fun add(frame: ByteArray, ptsSec: Double) {
            pts.add(ptsSec)
            var lumSum = 0L
            curCells.fill(0.0)
            for (y in 0 until H) {
                val cy = min(G - 1, y / ch)
                for (x in 0 until W) {
                    val v = frame[y * W + x].toInt() and 0xFF
                    lumSum += v
                    curCells[cy * G + min(G - 1, x / cw)] += v.toDouble()
                }
            }
            for (c in 0 until cells) curCells[c] /= ch.toDouble() * cw
            val lum = lumSum / (W * H).toDouble()
            if (count > 0) {
                var md = 0L
                for (i in frame.indices) md += abs((frame[i].toInt() and 0xFF) - (prev[i].toInt() and 0xFF))
                for (c in 0 until cells) { diffs[c] = max(0.0, curCells[c] - prevCells[c]); absd[c] = abs(curCells[c] - prevCells[c]) }
                diffs.sort(); absd.sort()
                motion.add(md / frame.size.toDouble())
                flash.add((diffs[cells - 1] + diffs[cells - 2] + diffs[cells - 3] + diffs[cells - 4]) / 4)
                var s = 0.0
                for (c in cells - 6 until cells) s += absd[c]
                cellChg.add(s / 6)
                lumUp.add(max(0.0, lum - prevLum))
            }
            prev = frame.copyOf()
            System.arraycopy(curCells, 0, prevCells, 0, cells)
            prevLum = lum
            count++
        }

        /** Feature i compares frame i+1 with frame i, so it carries frame i+1's timestamp. */
        fun result(): VideoFeatures {
            val t = pts.drop(1).take(motion.size)
            val k = min(t.size, motion.size)
            return VideoFeatures(
                t.take(k).toDoubleArray(),
                arrayOf(motion.take(k).toDoubleArray(), flash.take(k).toDoubleArray(), cellChg.take(k).toDoubleArray(), lumUp.take(k).toDoubleArray()),
            )
        }
    }
}
