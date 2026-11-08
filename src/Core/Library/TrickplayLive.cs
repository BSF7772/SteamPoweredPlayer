// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Library;

/// <summary>
/// Trickplay for a recording Steam is still writing. The generator is already incremental (it only decodes chunks no sheet has, and
/// refills the open last sheet), so a live session needs just one rule: the newest chunk may still be growing, so it waits for the next one.
/// </summary>
public static class TrickplayLive
{
    /// <summary>
    /// The part of <paramref name="media"/> that can be turned into tiles now. A finished recording is returned as it is; a live one
    /// without its newest chunk (Steam may still be writing it). Null when nothing is left.
    /// </summary>
    public static SessionMedia? Buildable(SessionMedia media)
    {
        if (media.ChunkCount == 0) return null;
        if (!media.Live) return media;
        var runs = media.Runs.ToList();
        var last = runs[^1];
        if (last.Count > 1) runs[^1] = last with { Last = last.Last - 1 };
        else runs.RemoveAt(runs.Count - 1);
        return runs.Count == 0 ? null : media with { Runs = runs };
    }
}
