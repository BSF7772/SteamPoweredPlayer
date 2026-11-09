// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.export

import kotlin.math.max
import kotlin.math.min
import kotlinx.serialization.Serializable

@Serializable
enum class LadderEngine { Auto, Cpu, Gpu }

object CodecMime {
    const val AVC = "video/avc"
    const val HEVC = "video/hevc"
    const val AV1 = "video/av01"
}

/** One encoder of this device. */
data class EncoderCap(val name: String, val mime: String, val hardware: Boolean, val maxWidth: Int, val maxHeight: Int)

/** What to encode with: the codec by name, the output size and frame rate cap, and the bitrates. */
data class EncodePlan(val encoder: String, val mime: String, val width: Int, val height: Int, val fpsCap: Float?, val videoBitsPerSecond: Int, val audioBitsPerSecond: Int)

/**
 * One choice of the export sheet: the maintainer's five tiers, the special presets, Save clip. Bits per pixel stand in for the desktop's quality numbers, because
 * Android's hardware encoders are driven by bitrate: the phone has no SVT-AV1, x264 or NVENC.
 */
data class LadderTier(
    val id: String,
    val name: String,
    val emoji: String,
    val tagline: String,
    /** Added to the file's name, so the file says which tier made it. Empty for special presets and Save clip. */
    val suffix: String,
    val bitsPerPixel: Double,
    val audioKbps: Int,
    val preferred: List<String>,
    val preferSoftware: Boolean = false,
    val maxShortSide: Int? = null,
    val fpsCap: Float? = null,
    val special: Boolean = false,
)

/** The approved ladder of 2026-11-06 and 2026-11-08. The default is the tier with the best performance per quality. Pure. */
object ExportLadder {
    const val SAVE_CLIP_ID = "save-clip"
    const val DEFAULT_ID = "medium"
    const val TARGET_ID = "target"

    val tiers = listOf(
        LadderTier("ultra-quality", "Ultra quality", "💎", "Visually lossless, in software where this phone can", "UQ", 0.127, 160, listOf(CodecMime.HEVC, CodecMime.AVC), preferSoftware = true),
        LadderTier("quality", "Quality", "🏆", "Archival look", "Q", 0.072, 128, listOf(CodecMime.AV1, CodecMime.HEVC, CodecMime.AVC)),
        LadderTier("medium", "Medium", "⚖️", "Best ratio", "M", 0.045, 96, listOf(CodecMime.AV1, CodecMime.HEVC, CodecMime.AVC)),
        LadderTier("performance", "Performance", "⚡", "Long clips", "P", 0.032, 96, listOf(CodecMime.HEVC, CodecMime.AVC)),
        LadderTier("ultra-performance", "Ultra performance", "📱", "720p, 30 fps, H.264 + AAC, to share", "UP", 0.11, 96, listOf(CodecMime.AVC), maxShortSide = 720, fpsCap = 30f),
    )

    val specials = listOf(
        LadderTier("archive", "Archive", "📦", "Smallest file, for long-term storage", "", 0.016, 64, listOf(CodecMime.AV1, CodecMime.HEVC, CodecMime.AVC), special = true),
        LadderTier("universal", "Universal H.264", "🌐", "H.264 + AAC, plays on anything", "", 0.09, 128, listOf(CodecMime.AVC), special = true),
        LadderTier(TARGET_ID, "Fit to size", "🎯", "Hits a file size, such as 10 MB", "", 0.045, 64, listOf(CodecMime.AV1, CodecMime.HEVC, CodecMime.AVC), special = true),
    )

    fun tier(id: String?): LadderTier? = (tiers + specials).firstOrNull { it.id == id }

    fun fileSuffix(id: String?): String = tiers.firstOrNull { it.id == id }?.let { "_" + it.suffix } ?: ""

    /**
     * The encoder and settings for [tier] on a source of this size. Null when no encoder of this device can do it on [engine]. An encoder that refuses the size gets a smaller
     * picture, the shape kept, as long as it stays usable: a phone's software encoders often stop below 1440p (S7).
     */
    fun plan(
        tier: LadderTier,
        engine: LadderEngine,
        encoders: List<EncoderCap>,
        sourceWidth: Int,
        sourceHeight: Int,
        sourceFps: Double,
        outputSeconds: Double,
        targetMegabytes: Double? = null,
    ): EncodePlan? {
        val (wanted, wantedH) = scaled(sourceWidth, sourceHeight, tier.maxShortSide)
        val usable = tier.preferred.flatMap { mime -> encoders.filter { it.mime == mime } }.filter { min(fitted(wanted, wantedH, it).first, fitted(wanted, wantedH, it).second) >= MIN_SHORT_SIDE }
        val allowed = when (engine) {
            LadderEngine.Cpu -> usable.filter { !it.hardware }
            LadderEngine.Gpu -> usable.filter { it.hardware }
            LadderEngine.Auto -> usable.filter { it.hardware || it.mime != CodecMime.AV1 }                    // software AV1 on a phone takes hours
        }
        // Order: an encoder that takes the full size first; among those the preferred codec; in it, hardware unless the tier prefers software.
        val chosen = listOf(allowed.filter { wanted <= it.maxWidth && wantedH <= it.maxHeight }, allowed).firstNotNullOfOrNull { group ->
            tier.preferred.firstNotNullOfOrNull { mime ->
                val same = group.filter { it.mime == mime }
                (if (tier.preferSoftware) same.firstOrNull { !it.hardware } else same.firstOrNull { it.hardware }) ?: same.firstOrNull()
            }
        } ?: return null
        val (w, h) = fitted(wanted, wantedH, chosen)
        val fps = min(sourceFps.takeIf { it > 0 } ?: 60.0, (tier.fpsCap ?: Float.MAX_VALUE).toDouble())
        var bits = (w.toLong() * h * fps * tier.bitsPerPixel).toLong()
        if (chosen.mime == CodecMime.AV1) bits = bits * 7 / 10
        if (chosen.mime == CodecMime.AVC && tier.id != "ultra-performance" && tier.id != "universal") bits = bits * 3 / 2
        if (tier.id == TARGET_ID && targetMegabytes != null) bits = targetBits(targetMegabytes, outputSeconds, tier.audioKbps)
        return EncodePlan(chosen.name, chosen.mime, w, h, tier.fpsCap, bits.coerceIn(MIN_BITS, Int.MAX_VALUE.toLong()).toInt(), tier.audioKbps * 1000)
    }

    /** The picture's share of a size budget, 3 % for the container. */
    fun targetBits(megabytes: Double, seconds: Double, audioKbps: Int): Long {
        if (seconds <= 0) return MIN_BITS
        val total = megabytes * 1024 * 1024 * 8 / seconds
        return max(MIN_BITS.toDouble(), total * 0.97 - audioKbps * 1000).toLong()
    }

    /** What the file will weigh. */
    fun estimateBytes(plan: EncodePlan, outputSeconds: Double): Long = ((plan.videoBitsPerSecond + plan.audioBitsPerSecond) / 8.0 * outputSeconds).toLong()

    /** Even numbers, the short side at most [maxShortSide], the shape kept. */
    fun scaled(width: Int, height: Int, maxShortSide: Int?): Pair<Int, Int> {
        val short = min(width, height)
        if (maxShortSide == null || short <= maxShortSide) return width to height
        val ratio = maxShortSide.toDouble() / short
        return even(width * ratio) to even(height * ratio)
    }

    /** The size [encoder] takes: smaller when it must, the shape kept, on even numbers. */
    private fun fitted(width: Int, height: Int, encoder: EncoderCap): Pair<Int, Int> {
        if (width <= encoder.maxWidth && height <= encoder.maxHeight) return width to height
        val ratio = min(encoder.maxWidth.toDouble() / width, encoder.maxHeight.toDouble() / height)
        return evenDown(width * ratio) to evenDown(height * ratio)
    }

    private fun evenDown(v: Double) = v.toInt() and 1.inv()

    private fun even(v: Double) = (Math.round(v / 2) * 2).toInt()

    private const val MIN_SHORT_SIDE = 480

    private const val MIN_BITS = 100_000L
}
