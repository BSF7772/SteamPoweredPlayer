// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Net;

/// <summary>
/// How much of a remote recording is on this PC. A chunk counts only when every file of it (both streams) is cached, so a partial
/// download ("around markers") is reported as what it is, not as a finished recording.
/// </summary>
/// <param name="Have">Chunks on this PC.</param>
/// <param name="Total">Chunks the recording has on the server.</param>
public sealed record DownloadCoverage(int Have, int Total, double SegmentSeconds)
{
    public bool IsComplete => Total > 0 && Have >= Total;
    public bool IsPartial => Have > 0 && Have < Total;
    public double Fraction => Total > 0 ? (double)Have / Total : 0;

    /// <summary>Playable seconds on this PC (gaps between parts are not counted).</summary>
    public double Seconds => Have * SegmentSeconds;

    /// <param name="chunkIsCached">True when every file of that chunk number is cached.</param>
    public static DownloadCoverage Of(SessionMedia media, Func<int, bool> chunkIsCached)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(chunkIsCached);
        var have = 0;
        var total = 0;
        foreach (var c in media.Chunks())
        {
            total++;
            if (chunkIsCached(c)) have++;
        }
        return new DownloadCoverage(have, total, media.SegmentSeconds);
    }
}
