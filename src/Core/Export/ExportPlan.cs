// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Export;

/// <summary>
/// The chunks an export reads for a range on the media (tfdt) axis. <see cref="SnappedStart"/>..<see cref="SnappedEnd"/> is what a
/// lossless export contains (the 3 s keyframe grid); a re-encode trims to <see cref="Start"/>..<see cref="End"/> inside it.
/// </summary>
/// <param name="GapSeconds">Chunks missing on disk inside the range: the output holds the picture over them.</param>
public sealed record ExportPlan(int FirstChunk, int LastChunk, double Start, double End, double SnappedStart, double SnappedEnd, double GapSeconds)
{
    /// <summary>
    /// Plans <paramref name="start"/>..<paramref name="end"/>, clamped to the chunks on disk. Null when no complete chunk overlaps the
    /// range or the range is empty.
    /// </summary>
    public static ExportPlan? For(SessionMedia media, double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start) return null;
        int? first = null, last = null;
        var count = 0;
        foreach (var chunk in media.Chunks())
        {
            if (media.ChunkEndSeconds(chunk) <= start || media.ChunkStartSeconds(chunk) >= end) continue;
            first ??= chunk;
            last = chunk;
            count++;
        }
        if (first is not { } a || last is not { } b) return null;
        var snappedStart = media.ChunkStartSeconds(a);
        var snappedEnd = media.ChunkEndSeconds(b);
        return new ExportPlan(a, b, Math.Max(start, snappedStart), Math.Min(end, snappedEnd), snappedStart, snappedEnd,
            (b - a + 1 - count) * media.SegmentSeconds);
    }

    /// <summary>The whole recording, or null when it has no chunks.</summary>
    public static ExportPlan? Whole(SessionMedia media) =>
        media.FirstChunk is { } a && media.LastChunk is { } b ? For(media, media.ChunkStartSeconds(a), media.ChunkEndSeconds(b)) : null;
}

/// <summary>Pure file-name and folder rules for exports.</summary>
public static class ExportPaths
{
    /// <summary>A safe file stem: invalid characters become spaces, whitespace collapses, no trailing dots, at most 120 characters.</summary>
    public static string SafeStem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Join(' ', new string([.. name.Select(c => invalid.Contains(c) ? ' ' : c)])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 120) clean = clean[..120];
        clean = clean.Trim().TrimEnd('.', ' ');
        return clean.Length == 0 ? "Export" : clean;
    }

    /// <summary>
    /// True when <paramref name="folder"/> is <paramref name="root"/> or below it (case-insensitive, normalised). Exports never write
    /// inside Steam's recording folder (AGENTS.md).
    /// </summary>
    public static bool IsInside(string folder, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var (f, r) = (Norm(folder), Norm(root));
        return f.Equals(r, StringComparison.OrdinalIgnoreCase)
            || f.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first of <c>stem.ext</c>, <c>stem (2).ext</c>, … that does not exist.</summary>
    public static string Unique(string folder, string stem, string extension)
    {
        var path = Path.Combine(folder, stem + extension);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}){extension}");
        return path;
    }
}
