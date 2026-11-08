// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

/**
 * Where a saved clip is meant to start and end, as in the desktop's `ClipBounds` (docs/steam-recording-format.md, "Saved clips keep whole chunks"). Steam
 * copies the 3 s chunks that cover the clip, and only the clip's own `session.mpd` says where it is, on the chunks' tfdt axis. Pure.
 */
object ClipBounds {
    /** Measured on 196 real clips: the tail past the intended end is 0.04 to 3.03 s. A longer one means the manifest and the chunks don't belong together. */
    const val TOLERANCE_SECONDS = 0.1

    private val duration = Regex("""^PT(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?$""")

    /** The intended end on the media axis; null when the manifest can't say or doesn't fit the chunks. [mpd] needs only its first few KB. */
    fun fromManifest(mpd: String?, lastChunkEndSeconds: Double, segmentSeconds: Double): Double? {
        if (!lastChunkEndSeconds.isFinite() || segmentSeconds <= 0) return null
        val (start, length) = manifest(mpd) ?: return null
        val end = start + length
        val tail = lastChunkEndSeconds - end
        return end.takeIf { tail > 0 && tail < segmentSeconds + TOLERANCE_SECONDS }
    }

    /** The intended start on the media axis; null under the same conditions, or when it isn't after the first chunk's start by less than one chunk. */
    fun startFromManifest(mpd: String?, firstChunkStartSeconds: Double, segmentSeconds: Double): Double? {
        if (!firstChunkStartSeconds.isFinite() || segmentSeconds <= 0) return null
        val (start, _) = manifest(mpd) ?: return null
        val head = start - firstChunkStartSeconds
        return start.takeIf { head > 0 && head < segmentSeconds + TOLERANCE_SECONDS }
    }

    /** [media] with its limits from [mpd]; the same instance when there are none. */
    fun apply(media: SessionMedia, mpd: String?): SessionMedia {
        val first = media.firstChunk ?: return media
        val last = media.lastChunk ?: return media
        var start = startFromManifest(mpd, media.chunkStartSeconds(first), media.segmentSeconds)
        val end = fromManifest(mpd, media.chunkEndSeconds(last), media.segmentSeconds)
        if (start != null && (end ?: media.chunkEndSeconds(last)) <= start) start = null     // nothing would be left: the manifest doesn't fit these chunks
        return if (start == null && end == null) media else media.copy(startLimitSeconds = start, endLimitSeconds = end)
    }

    /** An ISO 8601 time duration (`PT1H2M3.5S`) in seconds; null for anything else (days, negatives, text). */
    fun parseDuration(text: String?): Double? {
        val m = duration.find(text ?: return null) ?: return null
        val (hours, minutes, seconds) = m.destructured
        if (hours.isEmpty() && minutes.isEmpty() && seconds.isEmpty()) return null
        val total = (hours.toDoubleOrNull() ?: 0.0) * 3600 + (minutes.toDoubleOrNull() ?: 0.0) * 60 + (seconds.toDoubleOrNull() ?: 0.0)
        return total.takeIf { it.isFinite() }
    }

    /** The Period start and the duration of a finished manifest; null for a live, zero-filled or incomplete one. */
    private fun manifest(mpd: String?): Pair<Double, Double>? {
        if (mpd.isNullOrBlank()) return null
        val tag = element(mpd, "MPD") ?: return null
        if (attribute(tag, "type") == "dynamic") return null
        val length = parseDuration(attribute(tag, "mediaPresentationDuration"))?.takeIf { it > 0 } ?: return null
        val start = parseDuration(attribute(element(mpd, "Period") ?: return null, "start")) ?: return null
        return start to length
    }

    private fun element(xml: String, name: String): String? = Regex("""<$name(?=[\s>/])[^>]*>""").find(xml)?.value

    private fun attribute(tag: String, name: String): String? {
        val m = Regex("""\s$name\s*=\s*(?:"([^"]*)"|'([^']*)')""").find(tag) ?: return null
        return m.groups[1]?.value ?: m.groups[2]?.value
    }
}
