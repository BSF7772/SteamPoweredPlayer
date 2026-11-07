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
/// not zero-filled. Chunk N starts at media time (N − 1) · <see cref="SegmentSeconds"/> on the tfdt axis.
/// </summary>
/// <param name="Folder">Absolute path of the recording folder that holds <c>init-stream{0,1}.m4s</c> and the chunks.</param>
/// <param name="Runs">Complete chunks, sorted, non-overlapping, non-adjacent.</param>
/// <param name="SegmentSeconds">Chunk length (3 s for every recording seen so far).</param>
/// <param name="Live">Steam is still writing this session (dynamic manifest and a recent newest chunk).</param>
/// <param name="Temporary">A background-cache session that Steam trims on its own.</param>
public sealed record SessionMedia(string Folder, IReadOnlyList<SegmentRun> Runs, double SegmentSeconds, bool Live, bool Temporary)
{
    public const double DefaultSegmentSeconds = 3.0;

    public int ChunkCount => Runs.Sum(r => r.Count);
    public int? FirstChunk => Runs.Count > 0 ? Runs[0].First : null;
    public int? LastChunk => Runs.Count > 0 ? Runs[^1].Last : null;

    /// <summary>Playable length (chunks × segment length); gaps are not counted. mpv's own duration is unreliable with an EDL.</summary>
    public double DurationSeconds => ChunkCount * SegmentSeconds;

    public bool Contains(int chunk) => Runs.Any(r => r.Contains(chunk));

    /// <summary>All complete chunk numbers in order.</summary>
    public IEnumerable<int> Chunks() => Runs.SelectMany(r => Enumerable.Range(r.First, r.Count));

    /// <summary>Start of chunk <paramref name="chunk"/> on the tfdt media axis.</summary>
    public double ChunkStartSeconds(int chunk) => (chunk - 1) * SegmentSeconds;
}
