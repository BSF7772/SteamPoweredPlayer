// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Export;

/// <summary>One finished export file in the export folder.</summary>
public sealed record ExportFile(string Path, string Name, long SizeBytes, DateTimeOffset ModifiedUtc);

/// <summary>
/// Lists the video files in the user's export folder (read-only). Pure file-system reading: no UI, never writes or deletes.
/// </summary>
public static class ExportFolderListing
{
    private static readonly string[] Extensions = [".mp4", ".mkv", ".webm"];

    /// <summary>Hidden working folders of a running export start with this; their contents are never listed.</summary>
    public const string WorkingPrefix = ".spp-export-";

    public static bool IsVideo(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The video files directly in <paramref name="folder"/>, newest first. A missing or unreadable folder gives an empty list, and a
    /// file that vanishes while it is read is skipped. Hidden files and <c>.spp-export-*</c> working files are skipped.
    /// </summary>
    public static IReadOnlyList<ExportFile> List(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return [];
        var result = new List<ExportFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var name = System.IO.Path.GetFileName(path);
                if (name.StartsWith(WorkingPrefix, StringComparison.OrdinalIgnoreCase) || !IsVideo(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || (info.Attributes & FileAttributes.Hidden) != 0) continue;
                    result.Add(new(path, name, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return [.. result.OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Exports of <paramref name="game"/>: the file name starts with the sanitised game name (case-insensitive) followed by a space or
    /// the end of the stem. Games whose names share a prefix ("Doom" and "Doom Eternal") cannot be told apart by name alone.
    /// </summary>
    public static IReadOnlyList<ExportFile> ForGame(IEnumerable<ExportFile> files, string? game)
    {
        if (string.IsNullOrWhiteSpace(game)) return [];
        var stem = ExportPaths.SafeStem(game);
        return [.. files.Where(f =>
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(f.Name);
            return n.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && (n.Length == stem.Length || n[stem.Length] == ' ');
        })];
    }
}
