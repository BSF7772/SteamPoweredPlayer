// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Playback;

public enum ReloadReason
{
    /// <summary>Keep playing the current EDL.</summary>
    None,
    /// <summary>The playhead is outside the loaded window (a seek, or an empty window).</summary>
    OutsideWindow,
    /// <summary>The first or last chunk of the window is no longer on disk (Steam trimmed it).</summary>
    WindowStale,
    /// <summary>The playhead is within <see cref="EdlWindow.ReloadMarginChunks"/> of the window's end and more chunks exist.</summary>
    NearWindowEnd,
    /// <summary>Enough media seconds were played since the last load to bound mpv's RAM and open handles.</summary>
    Periodic,
}

/// <summary>
/// Which ~60 chunks go into the EDL, and when a loaded EDL must be rebuilt. mpv never closes played chunks (RAM grows about 0.23 MB
/// per media second, open handles about 1.7 per chunk), so playback uses a window and reloads (S2: a reload costs about 95 ms).
/// Pure: no I/O.
/// </summary>
public static class EdlWindow
{
    public const int DefaultCount = 60;

    /// <summary>Of a window, the part before the playhead is <c>count / LookBehindDivisor</c> chunks (45 s of 180 s by default): playback is mostly forward.</summary>
    public const int LookBehindDivisor = 4;

    /// <summary>Rebuild when the playhead is at most this many chunks (30 s) from the window's end, so the reload happens well before EOF.</summary>
    public const int ReloadMarginChunks = 10;

    /// <summary>Rebuild after this many media seconds of sequential play (S2: 431 MB after 16 media minutes of one EDL, 326 MB after a reload).</summary>
    public const double PeriodicReloadMediaSeconds = 600;

    /// <summary>
    /// About <paramref name="count"/> complete chunk numbers around <paramref name="positionSeconds"/> (media/tfdt time), ascending,
    /// spanning runs and gaps, clamped at the session edges (the window shifts instead of shrinking). A position before the session
    /// maps to the first chunk, past it to the last, inside a gap to the next chunk after it.
    /// </summary>
    public static IReadOnlyList<int> Window(SessionMedia media, double positionSeconds, int count = DefaultCount)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);
        var total = media.ChunkCount;
        if (total == 0) return [];

        var anchor = IndexOfPosition(media, positionSeconds);
        var start = anchor - count / LookBehindDivisor;
        start = Math.Min(start, total - count);          // shift back at the end ...
        start = Math.Max(start, 0);                      // ... and forward at the start
        var take = Math.Min(count, total - start);

        var result = new List<int>(take);
        var skipped = 0;
        foreach (var run in media.Runs)
        {
            if (result.Count == take) break;
            if (skipped + run.Count <= start) { skipped += run.Count; continue; }
            var from = run.First + Math.Max(0, start - skipped);
            for (var n = from; n <= run.Last && result.Count < take; n++) result.Add(n);
            skipped += run.Count;
        }
        return result;
    }

    /// <summary>Should the loaded EDL (built over <paramref name="window"/>) be rebuilt now? Reasons are checked in enum order after OutsideWindow.</summary>
    /// <param name="window">Chunk numbers of the loaded EDL (ascending).</param>
    /// <param name="media">The current scan result (it changes while Steam trims or a live session grows).</param>
    /// <param name="playheadSeconds">mpv <c>time-pos</c> (media time).</param>
    /// <param name="mediaSecondsSinceLoad">Media seconds played since the EDL was loaded (speed-scaled by the caller as it sees fit).</param>
    public static ReloadReason ShouldReload(IReadOnlyList<int> window, SessionMedia media, double playheadSeconds, double mediaSecondsSinceLoad)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(media);
        if (media.ChunkCount == 0) return ReloadReason.None;
        if (window.Count == 0) return ReloadReason.OutsideWindow;

        var first = window[0];
        var last = window[^1];
        var atSessionStart = first == media.FirstChunk;     // the lead part covers the axis before the first chunk: nothing to load there
        var atSessionEnd = last == media.LastChunk;         // nothing more to load after it (a live session growing makes this false again)

        var chunk = ChunkOf(playheadSeconds, media.SegmentSeconds);
        if ((!atSessionStart && chunk < first) || (!atSessionEnd && chunk > last)) return ReloadReason.OutsideWindow;
        if (!media.Contains(first) || !media.Contains(last)) return ReloadReason.WindowStale;
        if (!atSessionEnd && last - chunk <= ReloadMarginChunks) return ReloadReason.NearWindowEnd;
        if (mediaSecondsSinceLoad >= PeriodicReloadMediaSeconds) return ReloadReason.Periodic;
        return ReloadReason.None;
    }

    /// <summary>Chunk number whose 3 s interval contains the media time (chunk N covers [(N−1)·S, N·S)). Clamped to the int range; NaN counts as 0.</summary>
    private static long ChunkOf(double seconds, double segmentSeconds)
    {
        if (double.IsNaN(seconds)) seconds = 0;
        var c = Math.Floor(seconds / segmentSeconds) + 1;
        return (long)Math.Clamp(c, -1e9, 1e9);
    }

    /// <summary>Index (0-based, over all complete chunks in order) of the chunk at the position, or of the next complete chunk if it is in a gap.</summary>
    private static int IndexOfPosition(SessionMedia media, double positionSeconds)
    {
        var chunk = ChunkOf(positionSeconds, media.SegmentSeconds);
        var index = 0;
        foreach (var run in media.Runs)
        {
            if (chunk < run.First) return index;                       // before the session, or inside the gap before this run
            if (chunk <= run.Last) return index + (int)(chunk - run.First);
            index += run.Count;
        }
        return index - 1;                                               // past the end
    }
}
