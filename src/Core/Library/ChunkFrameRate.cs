// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>
/// The video's frame rate from the chunks on disk: frames per chunk (the <c>trun</c> sample counts of its fragments) over the segment
/// length. mpv's <c>container-fps</c> can't be used for this: on a real 60 fps session it reports about 120 after some reloads.
/// </summary>
public static class ChunkFrameRate
{
    private const int MaxMoofBytes = 4 * 1024 * 1024;
    private const int ChunksMeasured = 3;

    /// <summary>
    /// Frames per second of <paramref name="media"/>, from the fullest of its first chunks (capture jitter can drop a frame or two from a
    /// chunk, never add one). Null when no chunk can be read: Steam may have just removed them.
    /// </summary>
    public static double? Measure(SessionMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (!(media.SegmentSeconds > 0)) return null;
        var most = 0L;
        foreach (var chunk in media.Chunks().Take(ChunksMeasured))
        {
            try
            {
                var path = Path.Combine(media.Folder, FolderNames.ChunkFile(0, chunk));
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                most = Math.Max(most, CountFrames(fs));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* trimmed by Steam: the other chunks decide */ }
        }
        return most > 0 ? most / media.SegmentSeconds : null;
    }

    /// <summary>The total sample count of all fragments in a chunk; 0 when it holds no readable fragment.</summary>
    public static long CountFrames(Stream chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var total = 0L;
        var header = new byte[16];
        var pos = 0L;
        while (pos + 8 <= chunk.Length)
        {
            chunk.Position = pos;
            chunk.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerSize = 8;
            if (size == 1)
            {
                if (pos + 16 > chunk.Length) break;
                chunk.ReadExactly(header, 8, 8);
                size = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0) size = chunk.Length - pos;
            if (size < headerSize || pos + size > chunk.Length) break;       // damaged, or still being written

            if (Encoding.ASCII.GetString(header, 4, 4) == "moof" && size - headerSize <= MaxMoofBytes)
            {
                var moof = new byte[size - headerSize];
                chunk.ReadExactly(moof);
                total += CountInBoxes(moof);
            }
            pos += size;
        }
        return total;
    }

    /// <summary>Sums <c>trun</c> sample counts in a <c>moof</c> payload, descending into <c>traf</c>.</summary>
    private static long CountInBoxes(ReadOnlySpan<byte> boxes)
    {
        var total = 0L;
        var pos = 0;
        while (pos + 8 <= boxes.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(boxes[pos..]);
            if (size < 8 || size > boxes.Length - pos) break;
            var body = boxes.Slice(pos + 8, (int)size - 8);
            var type = Encoding.ASCII.GetString(boxes.Slice(pos + 4, 4));
            if (type == "traf") total += CountInBoxes(body);
            else if (type == "trun" && body.Length >= 8) total += BinaryPrimitives.ReadUInt32BigEndian(body[4..]);   // after version + flags
            pos += (int)size;
        }
        return total;
    }
}
