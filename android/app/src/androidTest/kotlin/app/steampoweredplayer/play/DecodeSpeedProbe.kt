// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.play

import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaExtractor
import android.media.MediaFormat
import android.util.Log
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Opt-in, and a measurement rather than a check: how fast each decoder of this device or emulator takes a real fMP4 file, without any display in the way.
 * Push `<name>.mp4` (an init segment and its chunks, concatenated) to the test app's external files and pass `-e sppDecodeFile <name>.mp4`. Logs counts only.
 */
class DecodeSpeedProbe {
    @Test fun decoders_on_a_real_file() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val name = InstrumentationRegistry.getArguments().getString("sppDecodeFile")
        assumeTrue("no sppDecodeFile argument", !name.isNullOrEmpty())
        val file = File(instrumentation.targetContext.getExternalFilesDir(null), name!!)
        assumeTrue("file not there", file.exists())

        val extractor = MediaExtractor().apply { setDataSource(file.path) }
        val track = (0 until extractor.trackCount).first { extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME)!!.startsWith("video/") }
        val format = extractor.getTrackFormat(track)
        val mime = format.getString(MediaFormat.KEY_MIME)!!
        extractor.release()
        val decoders = MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos.filter { !it.isEncoder && mime in it.supportedTypes }
        for (info in decoders) {
            val caps = info.getCapabilitiesForType(mime).videoCapabilities
            val supported = caps.isSizeSupported(format.getInteger(MediaFormat.KEY_WIDTH), format.getInteger(MediaFormat.KEY_HEIGHT))
            Log.i(TAG, "${info.name} hw=${info.isHardwareAccelerated} swOnly=${info.isSoftwareOnly} sizeOk=$supported maxW=${caps.supportedWidths.upper} maxH=${caps.supportedHeights.upper}")
            if (!supported) continue
            try {
                decode(info.name, file, track, format)
            } catch (e: Exception) {
                Log.w(TAG, "${info.name} failed: ${e.javaClass.simpleName}: ${e.message}")
            }
        }
    }

    private fun decode(codecName: String, file: File, track: Int, format: MediaFormat) {
        val extractor = MediaExtractor().apply {
            setDataSource(file.path)
            selectTrack(track)
        }
        val codec = MediaCodec.createByCodecName(codecName)
        codec.configure(format, null, null, 0)
        codec.start()
        val info = MediaCodec.BufferInfo()
        var inputDone = false
        var outputDone = false
        var frames = 0
        var first = Long.MAX_VALUE
        var last = Long.MIN_VALUE
        val started = System.nanoTime()
        while (!outputDone && System.nanoTime() - started < LIMIT_NANOS) {
            if (!inputDone) {
                val i = codec.dequeueInputBuffer(5000)
                if (i >= 0) {
                    val n = extractor.readSampleData(codec.getInputBuffer(i)!!, 0)
                    if (n < 0) {
                        codec.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                        inputDone = true
                    } else {
                        codec.queueInputBuffer(i, 0, n, extractor.sampleTime, 0)
                        extractor.advance()
                    }
                }
            }
            val o = codec.dequeueOutputBuffer(info, 5000)
            if (o >= 0) {
                if (info.size > 0) {
                    frames++
                    first = minOf(first, info.presentationTimeUs)
                    last = maxOf(last, info.presentationTimeUs)
                }
                codec.releaseOutputBuffer(o, false)
                outputDone = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
            }
        }
        val seconds = (System.nanoTime() - started) / 1e9
        val media = if (frames > 1) (last - first) / 1e6 else 0.0
        Log.i(TAG, "$codecName: $frames frames in ${"%.2f".format(seconds)} s = ${"%.1f".format(frames / seconds)} fps, ${"%.2f".format(media / seconds)}x realtime, finished=$outputDone")
        codec.stop()
        codec.release()
        extractor.release()
    }

    private companion object {
        const val TAG = "SppDecodeProbe"
        const val LIMIT_NANOS = 40_000_000_000L
    }
}
