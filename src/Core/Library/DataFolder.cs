// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace SteamPoweredPlayer.Core.Library;

public enum DataFolderProblem
{
    None,
    /// <summary>Empty, whitespace, relative, or not a valid path.</summary>
    InvalidPath,
    /// <summary>The folder is, or lies inside, <c>BackgroundRecordPath</c> (Steam's tree; we never write there).</summary>
    InsideRecordingPath,
    /// <summary><c>BackgroundRecordPath</c> lies inside the folder (it would put Steam's tree inside ours).</summary>
    ContainsRecordingPath,
    /// <summary><c>BackgroundRecordPath</c> is a drive or share root: no sibling exists, the user must pick a folder elsewhere.</summary>
    RecordingPathIsRoot,
}

/// <summary>Result of <see cref="DataFolder.Validate"/> / <see cref="DataFolder.Propose"/>. <see cref="Path"/> is the normalised full path when known.</summary>
public sealed record DataFolderCheck(DataFolderProblem Problem, string? Path)
{
    public bool IsValid => Problem == DataFolderProblem.None;
}

/// <summary>
/// The folder where the desktop (the only writer, plan D11) keeps <c>library.json</c>, <c>library-id</c>, <c>art/</c>, <c>trick/</c>,
/// <c>proxies/</c> and <c>edits/</c>. Pure path logic plus the <c>library-id</c> file; no UI.
/// </summary>
public sealed class DataFolder
{
    public const string FolderName = "SteamPoweredPlayer";
    public const string LibraryJsonName = "library.json";
    public const string LibraryIdName = "library-id";
    public const string ScanCacheName = "scan-cache.json";

    public string Root { get; }
    public string BackgroundRecordPath { get; }

    private DataFolder(string root, string backgroundRecordPath)
    {
        Root = root;
        BackgroundRecordPath = backgroundRecordPath;
    }

    public string LibraryJson => System.IO.Path.Combine(Root, LibraryJsonName);
    public string LibraryJsonTemp => LibraryJson + ".tmp";
    /// <summary>Desktop-only scan cache (<see cref="ScanCache"/>); phones never read it.</summary>
    public string ScanCacheJson => System.IO.Path.Combine(Root, ScanCacheName);
    public string ScanCacheJsonTemp => ScanCacheJson + ".tmp";
    public string LibraryIdFile => System.IO.Path.Combine(Root, LibraryIdName);
    public string ArtDir => System.IO.Path.Combine(Root, "art");
    public string TrickDir => System.IO.Path.Combine(Root, "trick");
    public string ProxiesDir => System.IO.Path.Combine(Root, "proxies");
    public string EditsDir => System.IO.Path.Combine(Root, "edits");

    /// <summary>Per-game art folder. <paramref name="gameId"/> must be decimal digits (it becomes a folder name).</summary>
    public string ArtDirFor(string gameId)
    {
        if (!IsSafeGameId(gameId)) throw new ArgumentException("gameId must be decimal digits", nameof(gameId));
        return System.IO.Path.Combine(ArtDir, gameId);
    }

    public static bool IsSafeGameId(string? gameId) =>
        !string.IsNullOrEmpty(gameId) && gameId.Length <= 20 && gameId.All(char.IsAsciiDigit);

    /// <summary>
    /// First-run proposal: <c>SteamPoweredPlayer</c> as a sibling of <paramref name="backgroundRecordPath"/>, so a remote client that
    /// sees the recordings through a share sees the data folder through the same share.
    /// At a drive or share root there is no sibling, and <c>&lt;root&gt;\SteamPoweredPlayer</c> would be inside the recording path, so
    /// the result is <see cref="DataFolderProblem.RecordingPathIsRoot"/> with no path: the user must choose (typically another drive).
    /// </summary>
    public static DataFolderCheck Propose(string backgroundRecordPath)
    {
        var brp = Normalize(backgroundRecordPath);
        if (brp is null) return new(DataFolderProblem.InvalidPath, null);
        var parent = System.IO.Path.GetDirectoryName(brp);
        if (parent is null) return new(DataFolderProblem.RecordingPathIsRoot, null);
        return Validate(System.IO.Path.Combine(parent, FolderName), backgroundRecordPath);
    }

    /// <summary>
    /// Rejects a folder that equals or lies inside <paramref name="backgroundRecordPath"/>, or that contains it (we would own Steam's tree).
    /// Full paths, case-insensitive, trailing separators, <c>.</c> and <c>..</c> are resolved first. Symlinks and junctions are not
    /// resolved (documented limitation: the user chooses the folder, Core only guards against the lexical overlap).
    /// </summary>
    public static DataFolderCheck Validate(string candidate, string backgroundRecordPath)
    {
        var dir = Normalize(candidate);
        var brp = Normalize(backgroundRecordPath);
        if (dir is null || brp is null) return new(DataFolderProblem.InvalidPath, dir);
        if (IsSameOrInside(dir, brp)) return new(DataFolderProblem.InsideRecordingPath, dir);
        if (IsSameOrInside(brp, dir)) return new(DataFolderProblem.ContainsRecordingPath, dir);
        return new(DataFolderProblem.None, dir);
    }

    /// <summary>Validates and wraps. Throws <see cref="ArgumentException"/> when <see cref="Validate"/> fails. Creates nothing.</summary>
    public static DataFolder Open(string candidate, string backgroundRecordPath)
    {
        var check = Validate(candidate, backgroundRecordPath);
        if (!check.IsValid) throw new ArgumentException($"Data folder rejected: {check.Problem}", nameof(candidate));
        return new DataFolder(check.Path!, Normalize(backgroundRecordPath)!);
    }

    public void EnsureCreated()
    {
        // Re-check at the point of writing, in case the object outlived a changed setting.
        if (!Validate(Root, BackgroundRecordPath).IsValid) throw new InvalidOperationException("Data folder overlaps the recording path");
        Directory.CreateDirectory(Root);
    }

    /// <summary>The library GUID if <c>library-id</c> exists and is valid; null otherwise. Read-only: creates nothing.</summary>
    public Guid? ReadLibraryId() => TryReadId(out var id) ? id : null;

    /// <summary>Returns the library GUID, creating the file on first use (atomic create; a racing creator's value wins).</summary>
    public Guid ReadOrCreateLibraryId()
    {
        EnsureCreated();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (TryReadId(out var existing)) return existing;
            var fresh = Guid.NewGuid();
            try
            {
                using var fs = new FileStream(LibraryIdFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(fresh.ToString("D", CultureInfo.InvariantCulture) + "\n");
                fs.Write(bytes);
                fs.Flush(true);
                return fresh;
            }
            catch (IOException) when (File.Exists(LibraryIdFile)) { /* lost the race or still being written: read it */ Thread.Sleep(20); }
        }
        throw new IOException("library-id exists but cannot be read as a GUID");
    }

    private bool TryReadId(out Guid id)
    {
        id = default;
        try
        {
            using var fs = new FileStream(LibraryIdFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return Guid.TryParse(sr.ReadToEnd().Trim(), CultureInfo.InvariantCulture, out id);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return null;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return null;
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetPathRoot(full) ?? "";
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="ancestor"/> or lies below it (both normalised).</summary>
    private static bool IsSameOrInside(string path, string ancestor)
    {
        var a = ancestor.TrimEnd('\\', '/');
        if (path.TrimEnd('\\', '/').Equals(a, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Length > a.Length
            && path.StartsWith(a, StringComparison.OrdinalIgnoreCase)
            && (path[a.Length] == '\\' || path[a.Length] == '/');
    }
}
