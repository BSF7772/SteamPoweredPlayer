// SPDX-License-Identifier: GPL-3.0-or-later
package spp.spike.s8

import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * The exact decoder output the C# detector consumes, dumped by ffmpeg with the C# command lines
 * (see tools/dump-inputs.sh): audio.f32 (16 kHz mono f32le), video.gray (192x108 grey frames), video.pts (showinfo pts_time
 * lines). Running the analysis on these isolates the port from the decode substrate.
 */
object RawInputs {
    fun audio(f: File): FloatArray {
        val bb = ByteBuffer.wrap(f.readBytes()).order(ByteOrder.LITTLE_ENDIAN)
        return FloatArray(bb.remaining() / 4) { bb.getFloat() }
    }

    fun video(gray: File, ptsFile: File): AudioSync.VideoFeatures {
        val pts = ptsFile.readLines().mapNotNull { l ->
            val i = l.indexOf("pts_time:")
            if (i < 0) null else l.substring(i + 9).substringBefore(' ').toDoubleOrNull()
        }
        val acc = AudioSync.VideoAccumulator()
        val fs = AudioSync.W * AudioSync.H
        val bytes = gray.readBytes()
        val frames = bytes.size / fs
        // The C# loop pairs features with pts.Skip(1); the accumulator takes one pts per frame, so pass pts[i] (0 if missing).
        for (i in 0 until frames) acc.add(bytes.copyOfRange(i * fs, (i + 1) * fs), pts.getOrElse(i) { Double.NaN })
        val r = acc.result()
        // Trim to what C# would keep: min(pts.Count - 1, features).
        val k = minOf(r.pts.size, maxOf(0, pts.size - 1))
        return AudioSync.VideoFeatures(r.pts.copyOf(k), Array(4) { r.values[it].copyOf(k) })
    }
}
