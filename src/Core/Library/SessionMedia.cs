// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Library;

/// <summary>Contiguous complete chunk numbers <c>First..Last</c> (inclusive). A gap on disk splits a session into several runs.</summary>
public sealed record SegmentRun(int First, int Last)
{
    public int Count => Last - First + 1;
    public bool Contains(int chunk) => chunk >= First && chunk <= Last;
}

/// <summary>
/// The playable media of one recording folder, built from the chunk files on disk (never from <c>session.mpd</c>).
/// A chunk is complete when both <c>chunk-stream0-N</c> and <c>chunk-stream1-N</c> exist without <c>.tmp</c> and their heads are
/// not zero-filled. Chunk N starts at media time (N − 1) · <see cref="SegmentSeconds"/> + <see cref="AxisShiftSeconds"/> on the tfdt
/// axis: the shift is 0 for cache sessions and the real tfdt of chunk 1 for saved clips (they restart chunk numbers at 1 but keep the
/// session's absolute tfdt, docs/steam-recording-format.md "Media").
/// </summary>
/// <param name="Folder">Absolute path of the recording folder that holds <c>init-stream{0,1}.m4s</c> and the chunks.</param>
/// <param name="Runs">Complete chunks, sorted, non-overlapping, non-adjacent.</param>
/// <param name="SegmentSeconds">Chunk length (3 s for every recording seen so far).</param>
/// <param name="Live">Steam is still writing this session (dynamic manifest and a recent newest chunk).</param>
/// <param name="Temporary">A background-cache session that Steam trims on its own.</param>
/// <param name="AxisShiftSeconds">tfdt of the first chunk on disk minus (first − 1)·S (see <see cref="AxisShiftFor"/>). It does not change
/// when the head is trimmed, so <c>media with { Runs = … }</c> stays correct.</param>
/// <param name="BitsPerSecond">Measured average bitrate of the complete chunks (both streams), or null when unknown; sizes mpv's demuxer
/// cache (<see cref="Playback.DemuxerCache"/>).</param>
/// <param name="EndLimitSeconds">Saved clips only: where Steam meant the clip to end (media time), from <see cref="ClipBounds"/>. A clip keeps
/// whole chunks, so its last chunk runs 0 to 3 s past it (picture and sound alike). Null for a cache session, for a clip whose manifest
/// can't say, and whenever it isn't before the last chunk's end.</param>
/// <param name="StartLimitSeconds">Saved clips only: where Steam meant the clip to start, the same way. Its first chunk starts 0 to 3 s before it.</param>
public sealed record SessionMedia(string Folder, IReadOnlyList<SegmentRun> Runs, double SegmentSeconds, bool Live, bool Temporary,
    double AxisShiftSeconds = 0, double? BitsPerSecond = null, double? EndLimitSeconds = null, double? StartLimitSeconds = null)
{
    public const double DefaultSegmentSeconds = 3.0;

    public int ChunkCount => Runs.Sum(r => r.Count);
    public int? FirstChunk => Runs.Count > 0 ? Runs[0].First : null;
    public int? LastChunk => Runs.Count > 0 ? Runs[^1].Last : null;

    /// <summary>
    /// Playable length (chunks × segment length, less what a saved clip's limits cut off its first and last chunks); gaps are
    /// not counted. mpv's own duration is unreliable with an EDL.
    /// </summary>
    public double DurationSeconds => ChunkCount * SegmentSeconds - TrimmedHeadSeconds - TrimmedTailSeconds;

    /// <summary>
    /// The start of the playable media on the media axis: the first chunk's start, or the clip's intended start when that is later. 0
    /// without chunks. Playback, the timeline, seeking, markers and every export start here.
    /// </summary>
    public double StartSeconds => FirstChunk is { } f ? Math.Max(ChunkStartSeconds(f), StartLimitSeconds ?? double.NegativeInfinity) : 0;

    /// <summary>How much of the first chunk lies before <see cref="StartSeconds"/> (0 without a limit).</summary>
    public double TrimmedHeadSeconds => FirstChunk is { } f ? Math.Max(0, StartSeconds - ChunkStartSeconds(f)) : 0;

    /// <summary>
    /// The end of the playable media on the media axis: the last chunk's end, or the clip's intended end when that is earlier. 0 without
    /// chunks. Playback, the timeline, seeking, markers and every export stop here.
    /// </summary>
    public double EndSeconds => LastChunk is { } l ? Math.Min(ChunkEndSeconds(l), EndLimitSeconds ?? double.PositiveInfinity) : 0;

    /// <summary>How much of the last chunk lies past <see cref="EndSeconds"/> (0 without a limit).</summary>
    public double TrimmedTailSeconds => LastChunk is { } l ? Math.Max(0, ChunkEndSeconds(l) - EndSeconds) : 0;

    /// <summary>Media (tfdt) time of the first chunk on disk; 0 when there is none.</summary>
    public double TfdtOriginSeconds => FirstChunk is { } f ? ChunkStartSeconds(f) : 0;

    public bool Contains(int chunk) => Runs.Any(r => r.Contains(chunk));

    /// <summary>All complete chunk numbers in order.</summary>
    public IEnumerable<int> Chunks() => Runs.SelectMany(r => Enumerable.Range(r.First, r.Count));

    /// <summary>Start of chunk <paramref name="chunk"/> on the tfdt media axis.</summary>
    public double ChunkStartSeconds(int chunk) => (chunk - 1) * SegmentSeconds + AxisShiftSeconds;

    /// <summary>End of chunk <paramref name="chunk"/> on the tfdt media axis (= start of the next chunk number).</summary>
    public double ChunkEndSeconds(int chunk) => chunk * SegmentSeconds + AxisShiftSeconds;

    /// <summary>
    /// Chunk number whose interval [start, end) contains <paramref name="mediaSeconds"/> (it may not be on disk). NaN counts as the axis
    /// zero; the result is clamped to ±1e9.
    /// </summary>
    public long ChunkAt(double mediaSeconds)
    {
        if (double.IsNaN(mediaSeconds)) mediaSeconds = AxisShiftSeconds;
        var c = Math.Floor((mediaSeconds - AxisShiftSeconds) / SegmentSeconds) + 1;
        return (long)Math.Clamp(c, -1e9, 1e9);
    }

    /// <summary>
    /// The axis shift for a recording whose chunk <paramref name="chunk"/> starts at <paramref name="tfdtSeconds"/>: the difference to
    /// (chunk − 1)·S, rounded to a whole number of segments. Real chunks start late by game-capture jitter (up to 0.36 s seen at the
    /// trimmed head of a cache session, up to 0.07 s in clips), never by a fraction of a segment, so cache sessions give exactly 0
    /// (their EDLs stay byte-identical) and clips give whole segments (EDL part lengths stay exact). Never below −(chunk − 1)·S (the
    /// axis starts at 0 or later). A tfdt that is not finite or negative gives 0.
    /// </summary>
    public static double AxisShiftFor(double tfdtSeconds, int chunk, double segmentSeconds)
    {
        if (!double.IsFinite(tfdtSeconds) || tfdtSeconds < 0 || !(segmentSeconds > 0)) return 0;
        var raw = tfdtSeconds - (chunk - 1) * segmentSeconds;
        var shift = Math.Max(Math.Round(raw / segmentSeconds) * segmentSeconds, -(chunk - 1) * segmentSeconds);
        return shift == 0 ? 0 : shift;   // never −0 (it would print as "-0")
    }
}
