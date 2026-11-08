// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Net;

/// <summary>
/// Pure planning of which chunks a download-first remote library fetches, and in what order (plan §4.3 "priority queue").
/// A selection is a set of chunk numbers (a range, the whole recording, or the neighbourhood of markers); <see cref="Order"/>
/// turns it into the fetch order for the current playhead. A seek simply calls <see cref="Order"/> again with the new playhead.
/// </summary>
public static class ChunkPlan
{
    /// <summary>
    /// Fetch order: the chunk at the playhead (or the first selected chunk after it), then forward in time, then the selected
    /// chunks behind the playhead, nearest first (the likeliest backward seek target). Cached chunks are skipped; duplicates removed.
    /// </summary>
    public static IReadOnlyList<int> Order(IEnumerable<int> selection, int playheadChunk, Func<int, bool> isCached)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(isCached);
        var wanted = selection.Distinct().Where(c => !isCached(c)).ToList();
        var ahead = wanted.Where(c => c >= playheadChunk).OrderBy(c => c);
        var behind = wanted.Where(c => c < playheadChunk).OrderByDescending(c => c);
        return [.. ahead, .. behind];
    }

    /// <summary>The chunks of <paramref name="media"/> within <c>first..last</c> (inclusive).</summary>
    public static IReadOnlyList<int> Range(SessionMedia media, int first, int last) =>
        [.. media.Chunks().Where(c => c >= first && c <= last)];

    /// <summary>Every chunk of <paramref name="media"/>.</summary>
    public static IReadOnlyList<int> Whole(SessionMedia media) => [.. media.Chunks()];

    /// <summary>The chunk that covers media time <paramref name="seconds"/> (chunk N covers [(N−1)·len, N·len)). Negative times map to chunk 1.</summary>
    public static int ChunkAt(double seconds, double segmentSeconds) =>
        (int)Math.Floor(Math.Max(0.0, seconds) / segmentSeconds) + 1;

    /// <summary>
    /// The chunks within <paramref name="marginSeconds"/> before and after each marker (media-axis seconds, like
    /// <see cref="SessionMedia.ChunkStartSeconds"/>), limited to chunks present in <paramref name="media"/>; sorted, no duplicates.
    /// </summary>
    public static IReadOnlyList<int> AroundMarkers(SessionMedia media, IEnumerable<double> markerSeconds, double marginSeconds)
    {
        ArgumentNullException.ThrowIfNull(markerSeconds);
        return AroundMoments(media, markerSeconds.Select(t => (t, t)), marginSeconds, marginSeconds);
    }

    /// <summary>
    /// The chunks from <paramref name="beforeSeconds"/> before each moment's start to <paramref name="afterSeconds"/> after its end
    /// (media-axis seconds; a point has start = end), limited to chunks present in <paramref name="media"/>; sorted, no duplicates.
    /// </summary>
    public static IReadOnlyList<int> AroundMoments(SessionMedia media, IEnumerable<(double Start, double End)> moments, double beforeSeconds, double afterSeconds)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(moments);
        ArgumentOutOfRangeException.ThrowIfNegative(beforeSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSeconds);
        var wanted = new SortedSet<int>();
        foreach (var (start, end) in moments)
        {
            // media.ChunkAt honours the axis shift (saved clips sit at their real tfdt, not at (N−1)·len).
            var from = media.ChunkAt(start - beforeSeconds);
            var to = media.ChunkAt(Math.Max(start, end) + afterSeconds);
            foreach (var c in media.Chunks().Where(c => c >= from && c <= to)) wanted.Add(c);
        }
        return [.. wanted];
    }

    /// <summary>How many chunks to fetch at once: 3 on a healthy link, 2 once retries start to rise (two or more recently).</summary>
    public static int SuggestedParallelism(int recentRetries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recentRetries);
        return recentRetries >= 2 ? 2 : 3;
    }
}
