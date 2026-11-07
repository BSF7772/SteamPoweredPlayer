// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Library;

public enum FolderChangeKind { Created, Changed, Deleted, Renamed }

/// <summary>One file system notification. <see cref="OldFullPath"/> is set for <see cref="FolderChangeKind.Renamed"/>.</summary>
public readonly record struct FolderChange(FolderChangeKind Kind, string FullPath, string? OldFullPath = null);

/// <summary>
/// Seam over <see cref="FileSystemWatcher"/> so the library service is testable without real timing. Events may arrive on any thread
/// and must not be blocked. <see cref="Failed"/> means notifications may have been lost (buffer overflow) or the watch is dead
/// (the folder or share went away): the owner rescans everything and restarts the watcher.
/// </summary>
public interface IFolderWatcher : IDisposable
{
    event Action<FolderChange>? Changed;
    event Action<Exception>? Failed;

    /// <summary>Starts watching <paramref name="root"/> and everything below it. Throws when the folder cannot be watched.</summary>
    void Start(string root);
}

/// <summary>The real thing: one recursive <see cref="FileSystemWatcher"/> on the recording root (events only, plan D15: no polling).</summary>
public sealed class FileSystemFolderWatcher : IFolderWatcher
{
    /// <summary>64 KB, the largest buffer that is also valid over a network share (default is 8 KB: Steam writes a lot).</summary>
    public const int BufferSize = 64 * 1024;

    private FileSystemWatcher? _fsw;

    public event Action<FolderChange>? Changed;
    public event Action<Exception>? Failed;

    public void Start(string root)
    {
        var fsw = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = BufferSize,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        fsw.Created += (_, e) => Raise(new(FolderChangeKind.Created, e.FullPath));
        fsw.Changed += (_, e) => Raise(new(FolderChangeKind.Changed, e.FullPath));
        fsw.Deleted += (_, e) => Raise(new(FolderChangeKind.Deleted, e.FullPath));
        fsw.Renamed += (_, e) => Raise(new(FolderChangeKind.Renamed, e.FullPath, e.OldFullPath));
        fsw.Error += (_, e) => Failed?.Invoke(e.GetException());
        fsw.EnableRaisingEvents = true;
        _fsw = fsw;
    }

    private void Raise(FolderChange c)
    {
        try { Changed?.Invoke(c); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* a subscriber must not kill the watcher thread */ }
    }

    public void Dispose()
    {
        var f = Interlocked.Exchange(ref _fsw, null);
        if (f is null) return;
        f.EnableRaisingEvents = false;
        f.Dispose();
    }
}
