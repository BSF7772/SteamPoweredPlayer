// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// Reads <c>library.json</c> from the data folder with <see cref="IndexStore"/> and reloads it when the file changes
/// (FileSystemWatcher, no polling). Read-only: the scanner service is the writer.
/// </summary>
public sealed class IndexFileLibrarySource : ILibrarySource, IDisposable
{
    private readonly DataFolder _folder;
    private readonly IndexStore _store;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;

    public IndexFileLibrarySource(DataFolder folder)
    {
        _folder = folder;
        _store = new IndexStore(folder);
    }

    public LibraryIndex? Current { get; private set; }
    public string? Problem { get; private set; }
    public event EventHandler? IndexChanged;
    public event EventHandler<ScanProgressInfo>? ScanProgress;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await ReloadAsync(ct).ConfigureAwait(false);
        if (_watcher is not null || !Directory.Exists(_folder.Root)) return;
        _watcher = new FileSystemWatcher(_folder.Root, DataFolder.LibraryJsonName) { EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => Schedule();
        _watcher.Created += (_, _) => Schedule();
        _watcher.Renamed += (_, _) => Schedule();
    }

    /// <summary>A swap raises several events within milliseconds; wait for the burst to end, then load once.</summary>
    private void Schedule()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Delay(250, cts.Token).ContinueWith(async t =>
        {
            if (!t.IsCanceled) await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        }, TaskScheduler.Default);
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        ScanProgress?.Invoke(this, new ScanProgressInfo(true, "Reading the library…"));
        try
        {
            var parse = await _store.ReadAsync(ct).ConfigureAwait(false);
            switch (parse.Status)
            {
                case LibraryIndexStatus.Ok:
                    Problem = null;
                    Current = parse.Index;
                    break;
                case LibraryIndexStatus.NewerSchema:
                    Problem = "library.json was written by a newer version of the app. Update SteamPoweredPlayer.";
                    if (parse.Index is not null) Current = parse.Index;
                    break;
                case LibraryIndexStatus.Corrupt:
                    Problem = "library.json can't be read (" + (parse.Error ?? "corrupt") + "). The next scan rewrites it.";
                    break;
                default:
                    Problem = "No library yet. The first scan creates it.";
                    break;
            }
            IndexChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            ScanProgress?.Invoke(this, new ScanProgressInfo(false, "Ready"));
        }
    }

    public string? ResolveArt(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;
        // The index is data from a shared folder: never let a crafted path leave the data folder.
        var full = Path.GetFullPath(Path.Combine(_folder.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(_folder.Root).TrimEnd('\\') + "\\";
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    public IReadOnlyList<MarkerSpot> MarkersFor(RecordingEntry recording) => [];

    public void Dispose()
    {
        _debounce?.Cancel();
        _watcher?.Dispose();
    }
}
