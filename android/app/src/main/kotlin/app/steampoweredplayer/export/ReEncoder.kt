// SPDX-License-Identifier: GPL-3.0-or-later
@file:androidx.annotation.OptIn(markerClass = [androidx.media3.common.util.UnstableApi::class])

package app.steampoweredplayer.export

import android.content.Context
import android.media.MediaCodecList
import android.media.MediaFormat
import android.net.Uri
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.audio.AudioProcessor
import androidx.media3.common.audio.ChannelMixingAudioProcessor
import androidx.media3.common.audio.ChannelMixingMatrix
import androidx.media3.common.audio.SpeedProvider
import androidx.media3.common.util.Clock
import androidx.media3.effect.FrameDropEffect
import androidx.media3.effect.Presentation
import androidx.media3.exoplayer.mediacodec.MediaCodecSelector
import androidx.media3.transformer.AudioEncoderSettings
import androidx.media3.transformer.Codec
import androidx.media3.transformer.Composition
import androidx.media3.transformer.DefaultAssetLoaderFactory
import androidx.media3.transformer.DefaultDecoderFactory
import androidx.media3.transformer.DefaultEncoderFactory
import androidx.media3.transformer.EditedMediaItem
import androidx.media3.transformer.EditedMediaItemSequence
import androidx.media3.transformer.Effects
import androidx.media3.transformer.EncoderSelector
import androidx.media3.transformer.ExportException
import androidx.media3.transformer.ExportResult
import androidx.media3.transformer.ProgressHolder
import androidx.media3.transformer.Transformer
import androidx.media3.transformer.VideoEncoderSettings
import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.SessionMedia
import com.google.common.collect.ImmutableList
import java.io.File
import java.io.IOException
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlin.math.max
import kotlinx.coroutines.suspendCancellableCoroutine

/** The encoders this device has, as the ladder sees them. */
object DeviceEncoders {
    fun list(): List<EncoderCap> {
        val out = ArrayList<EncoderCap>()
        for (info in MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos) {
            if (!info.isEncoder) continue
            for (mime in info.supportedTypes) {
                if (mime != CodecMime.AVC && mime != CodecMime.HEVC && mime != CodecMime.AV1) continue
                val caps = info.getCapabilitiesForType(mime).videoCapabilities ?: continue
                out.add(EncoderCap(info.name, mime, info.isHardwareAccelerated, caps.supportedWidths.upper, caps.supportedHeights.upper))
            }
        }
        return out
    }
}

/** What a re-encoded export changes. */
data class ReEncodeEdits(val speed: Double = 1.0, val volumePercent: Double = 100.0, val muted: Boolean = false, val audioDelay: Double = 0.0)

/**
 * A re-encode with Media3 Transformer. Each kept range first becomes a plain MP4 of its whole chunks with the sync shift in its sound; the ranges then go through as one sequence,
 * clipped to their exact times. Colour (S7): full-range input goes through a software decoder, because a hardware decoder can clip it; [fastDecode] is the hidden switch to speed.
 */
class ReEncoder(private val context: Context, private val fastDecode: Boolean) {
    /** Suspends until [output] is written. Cancelling stops the encoder. */
    suspend fun run(media: SessionMedia, spans: List<ExportSpan>, plan: EncodePlan, edits: ReEncodeEdits, work: File, output: File, progress: (Double) -> Unit) {
        val plans = ExportSpans.plans(media, spans)
        if (plans.isEmpty()) throw IOException("Nothing to export: this range has no chunks.")
        work.mkdirs()
        val items = plans.mapIndexed { i, p -> item(media, p, plan, edits, File(work, "part-$i.mp4")) }
        val composition = Composition.Builder(EditedMediaItemSequence.Builder(items).build()).build()

        val thread = HandlerThread("spp-transformer").apply { start() }
        val handler = Handler(thread.looper)
        try {
            suspendCancellableCoroutine { continuation ->
                var transformer: Transformer? = null
                handler.post {
                    try {
                        val t = transformer(plan, thread, object : Transformer.Listener {
                            override fun onCompleted(composition: Composition, result: ExportResult) {
                                Log.i(TAG, "encoded with ${result.videoEncoderName}, ${result.videoFrameCount} frames, ${result.fileSizeBytes} bytes")
                                continuation.resume(Unit)
                            }

                            override fun onError(composition: Composition, result: ExportResult, e: ExportException) {
                                continuation.resumeWithException(IOException("${e.errorCodeName}: ${e.cause?.message ?: e.message}", e))
                            }
                        })
                        transformer = t
                        t.start(composition, output.absolutePath)
                        val holder = ProgressHolder()
                        // Transformer has no progress callback: asking it is the only way.
                        handler.post(object : Runnable {
                            override fun run() {
                                if (!continuation.isActive) return
                                if (t.getProgress(holder) == Transformer.PROGRESS_STATE_AVAILABLE) progress(holder.progress / 100.0)
                                handler.postDelayed(this, PROGRESS_EVERY_MILLIS)
                            }
                        })
                    } catch (e: Exception) {
                        continuation.resumeWithException(e)
                    }
                }
                continuation.invokeOnCancellation { handler.post { transformer?.cancel() } }
            }
        } finally {
            thread.quitSafely()
            work.deleteRecursively()
        }
        val expected = ExportSpans.outputSeconds(plans, edits.speed)
        val last = lastFrameSeconds(output)
        if (!ExportSpans.isWholePicture(last, expected)) {
            output.delete()
            Log.w(TAG, "picture ends at $last of $expected s")
            throw IOException("The picture came out shorter than the kept range. Nothing was saved.")
        }
    }

    /** The time of the file's last frame, by its own samples. */
    private fun lastFrameSeconds(file: File): Double {
        val extractor = android.media.MediaExtractor()
        try {
            extractor.setDataSource(file.path)
            val track = (0 until extractor.trackCount).first { extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME)!!.startsWith("video/") }
            extractor.selectTrack(track)
            var last = 0L
            while (extractor.sampleTime >= 0) {
                last = max(last, extractor.sampleTime)
                extractor.advance()
            }
            return last / 1e6
        } finally {
            extractor.release()
        }
    }

    private fun item(media: SessionMedia, p: ExportPlan, plan: EncodePlan, edits: ReEncodeEdits, file: File): EditedMediaItem {
        val folder = File(media.folder)
        val video = track(folder, 0, media.chunks().filter { it in p.firstChunk..p.lastChunk }, true)
        val (firstAudio, lastAudio) = ExportPlan.audioChunks(media, p, edits.audioDelay)
        val audio = track(folder, 1, media.chunks().filter { it in firstAudio..lastAudio }, false)
        val source = video.sourceSeconds()
        val origin = source.min()
        // Nothing trimmed here: t = 0 of this file is the first frame's time, and its sound already carries the sync shift.
        val whole = LosslessRange(origin, source.max() + 1.0, openHead = true, openTail = true)
        val (videoEdits, audioEdits) = LosslessEdits.compute(
            listOf(whole), StoredPackets(source, video.mediaTicks()), StoredPackets(audio.sourceSeconds(), audio.mediaTicks()), Mp4Remux.timescales(video, audio), edits.audioDelay,
        )
        Mp4Remux.write(file, video, audio, videoEdits, audioEdits)
        val clipping = MediaItem.ClippingConfiguration.Builder()
            .setStartPositionUs(max(0L, Math.round((p.start - origin) * 1e6)))
            .setEndPositionUs(Math.round((p.end - origin) * 1e6))
            .build()
        val mediaItem = MediaItem.Builder().setUri(Uri.fromFile(file)).setClippingConfiguration(clipping).build()
        val builder = EditedMediaItem.Builder(mediaItem).setRemoveAudio(edits.muted)
        if (edits.speed != 1.0) {
            val speed = edits.speed.toFloat()
            builder.setSpeed(object : SpeedProvider {
                override fun getSpeed(timeUs: Long) = speed
                override fun getNextSpeedChangeTimeUs(timeUs: Long) = C.TIME_UNSET
            })
        }
        val videoEffects = buildList {
            plan.fpsCap?.let { add(FrameDropEffect.createDefaultFrameDropEffect(it)) }
            add(Presentation.createForWidthAndHeight(plan.width, plan.height, Presentation.LAYOUT_SCALE_TO_FIT))
        }
        val audioEffects = ArrayList<AudioProcessor>()
        if (edits.volumePercent != 100.0 && !edits.muted) {
            val gain = (edits.volumePercent / 100).toFloat()
            audioEffects.add(ChannelMixingAudioProcessor().apply {
                for (channels in 1..2) putChannelMixingMatrix(ChannelMixingMatrix.createForConstantGain(channels, channels).scaleBy(gain))
            })
        }
        return builder.setEffects(Effects(audioEffects, videoEffects)).build()
    }

    private fun track(folder: File, stream: Int, chunks: List<Int>, isVideo: Boolean): Mp4Track {
        val init = InitSegment.parse(File(folder, FolderNames.initFile(stream)).readBytes())
        return Mp4Track(init, chunks.flatMap { Fmp4Chunk.samples(File(folder, FolderNames.chunkFile(stream, it)), init) }, isVideo)
    }

    private fun transformer(plan: EncodePlan, thread: HandlerThread, listener: Transformer.Listener): Transformer {
        val encoders = DefaultEncoderFactory.Builder(context)
            .setEnableFallback(true)
            .setRequestedVideoEncoderSettings(VideoEncoderSettings.Builder().setBitrate(plan.videoBitsPerSecond).build())
            .setRequestedAudioEncoderSettings(AudioEncoderSettings.Builder().setBitrate(plan.audioBitsPerSecond).build())
            .setVideoEncoderSelector { mime -> ImmutableList.copyOf(EncoderSelector.DEFAULT.selectEncoderInfos(mime).filter { it.name == plan.encoder }) }
            .build()
        val builder = Transformer.Builder(context)
            .setLooper(thread.looper)
            .setVideoMimeType(plan.mime)
            .setAudioMimeType(MimeTypes.AUDIO_AAC)
            .setEncoderFactory(encoders)
            .addListener(listener)
        builder.setAssetLoaderFactory(DefaultAssetLoaderFactory(context, decoders(), Clock.DEFAULT, null))
        return builder.build()
    }

    /** A software decoder for full-range input unless asked for speed: a hardware decoder's path to the surface can clip it (S7). */
    private fun decoders(): Codec.DecoderFactory {
        fun factory(keep: (androidx.media3.exoplayer.mediacodec.MediaCodecInfo) -> Boolean) = DefaultDecoderFactory.Builder(context).setMediaCodecSelector { mime, secure, tunneling ->
            val all = MediaCodecSelector.DEFAULT.getDecoderInfos(mime, secure, tunneling)
            all.filter(keep).ifEmpty { all }
        }.build()
        val hardware = factory { true }
        val software = factory { it.softwareOnly }
        return object : Codec.DecoderFactory by hardware {
            override fun createForVideoDecoding(format: Format, outputSurface: android.view.Surface, requestSdrToneMapping: Boolean, logSessionId: android.media.metrics.LogSessionId?): Codec {
                val full = format.colorInfo?.colorRange == C.COLOR_RANGE_FULL
                return (if (full && !fastDecode) software else hardware).createForVideoDecoding(format, outputSurface, requestSdrToneMapping, logSessionId)
            }
        }
    }

    private companion object {
        const val TAG = "SppExport"
        const val PROGRESS_EVERY_MILLIS = 500L
    }
}
