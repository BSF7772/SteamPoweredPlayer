// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Playback;

/// <summary>mpv demuxer-cache settings for one recording: read-ahead in seconds and the byte ceilings derived from its bitrate.</summary>
/// <param name="ReadaheadSeconds">mpv <c>demuxer-readahead-secs</c>.</param>
/// <param name="MaxBytes">mpv <c>demuxer-max-bytes</c> (forward).</param>
/// <param name="MaxBackBytes">mpv <c>demuxer-max-back-bytes</c> (behind the playhead).</param>
public sealed record DemuxerCachePlan(double ReadaheadSeconds, long MaxBytes, long MaxBackBytes)
{
    /// <summary>The most the demuxer cache can hold: forward + back.</summary>
    public long TotalBytes => MaxBytes + MaxBackBytes;
}

/// <summary>
/// Decision D-RAM (plan 2026-11-07 §0): the demuxer cache is sized in <b>seconds of media</b>, so its RAM is a function of the bitrate,
/// not a fixed number. Bytes = bitrate/8 × seconds × <see cref="Headroom"/>, clamped to [floor, ceiling]. RAM gate for the bench:
/// peak RSS ≤ base + <see cref="ExpectedCacheBytes"/>(bitrate) + margin. Pure.
/// </summary>
/// <remarks>
/// At Steam's typical 22 Mbit/s (2.75 MB/s): forward 20 s → 68.75 MB (≈ 65.6 MiB), back 10 s → 34.4 MB (≈ 32.8 MiB), i.e. the same as
/// the fixed 64 + 32 MiB used before. A 50 Mbit/s recording gets ≈ 149 + 75 MiB, a 4 Mbit/s one the floors (16 + 8 MiB).
/// </remarks>
public static class DemuxerCache
{
    /// <summary>Forward read-ahead: more than six 3 s chunks, plenty for local files and the download-first cache.</summary>
    public const double ReadaheadSeconds = 20;

    /// <summary>Kept behind the playhead (a short rewind stays cheap; longer ones are reloads anyway).</summary>
    public const double BackSeconds = 10;

    /// <summary>Bitrate peaks above the session average (game-capture VBR): the byte ceiling gets this factor of slack.</summary>
    public const double Headroom = 1.25;

    /// <summary>Used when the bitrate is unknown or implausible: Steam's typical HEVC 1440p60 bitrate.</summary>
    public const double FallbackBitsPerSecond = 22_000_000;

    public const long MinForwardBytes = 16L << 20, MaxForwardBytes = 256L << 20;
    public const long MinBackBytes = 8L << 20, MaxBackBytesCeiling = 128L << 20;

    /// <summary>Below / above this the measured bitrate is treated as unknown (a few chunks of black, or a broken size).</summary>
    public const double MinPlausibleBitsPerSecond = 100_000, MaxPlausibleBitsPerSecond = 1_000_000_000;

    /// <summary>The cache settings for a stream of <paramref name="bitsPerSecond"/> (null, NaN or implausible: <see cref="FallbackBitsPerSecond"/>).</summary>
    public static DemuxerCachePlan For(double? bitsPerSecond)
    {
        var bps = bitsPerSecond is { } b && double.IsFinite(b) && b >= MinPlausibleBitsPerSecond && b <= MaxPlausibleBitsPerSecond
            ? b : FallbackBitsPerSecond;
        var bytesPerSecond = bps / 8 * Headroom;
        return new(ReadaheadSeconds,
            Bytes(bytesPerSecond * ReadaheadSeconds, MinForwardBytes, MaxForwardBytes),
            Bytes(bytesPerSecond * BackSeconds, MinBackBytes, MaxBackBytesCeiling));
    }

    /// <summary>The settings for <paramref name="media"/> (its measured <see cref="SessionMedia.BitsPerSecond"/>).</summary>
    public static DemuxerCachePlan For(SessionMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return For(media.BitsPerSecond);
    }

    /// <summary>The most RAM the demuxer cache may take for this bitrate (forward + back ceilings), for the bench's RAM gate.</summary>
    public static long ExpectedCacheBytes(double? bitsPerSecond) => For(bitsPerSecond).TotalBytes;

    private static long Bytes(double value, long min, long max) => Math.Clamp((long)Math.Ceiling(value), min, max);
}
