// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.UI.Dispatching;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// The real library: Core's event-driven <see cref="LibraryService"/> behind <see cref="ILibrarySource"/>.
/// The saved index shows at once, a background scan refreshes it, and every Core event is marshalled to the UI thread here,
/// so pages and the shell can touch controls in their handlers. Markers and trickplay are loaded lazily, off the UI thread.
/// Never writes inside Steam's folders (Core guarantees it); with no confirmed data folder the library lives in memory.
/// </summary>
public sealed class LibraryServiceSource : ILibrarySource, IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly LibraryService _service;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _lock = new();
    private HashSet<string>? _pendingFolders;           // changed since the last UI dispatch; null = nothing pending
    private bool _dispatchQueued;

    private readonly Dictionary<string, IReadOnlyList<Marker>> _markers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _markerLoading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrickplayIndex> _trick = new(StringComparer.Ordinal);

    private volatile LibraryIndex? _current;
    private volatile string? _problem;
    private DataFolder? _data;
    private bool _started;
    private bool _scanActive;

    public LibraryServiceSource(DispatcherQueue ui, string? recordingRoot, SteamPaths? paths, uint? accountId, DataFolder? data)
    {
        _ui = ui;
        _data = data;
        GameNameResolver? names = paths is null ? null : new GameNameResolver(paths, accountId, IndexName);
        _service = new LibraryService(new LibraryServiceOptions { RecordingRoot = recordingRoot, DataFolder = data, Names = names });
        _service.IndexChanged += OnIndexChanged;
        _service.MediaChanged += OnMediaChanged;
        _service.StatusChanged += OnStatusChanged;
        _service.ProgressChanged += OnProgress;
    }

    private string? IndexName(ulong id) =>
        _current?.Games.FirstOrDefault(g => g.GameId == id.ToString(CultureInfo.InvariantCulture))?.Name;

    /// <summary>The confirmed data folder, or null while the library runs in memory.</summary>
    public DataFolder? Data { get { lock (_lock) return _data; } }

    public LibraryIndex? Current => _current;
    public string? Problem => _problem;

    public event EventHandler<IndexChangedArgs>? IndexChanged;
    public event EventHandler<MediaChangedArgs>? MediaChanged;
    public event EventHandler<string>? DerivedChanged;
    public event EventHandler<ScanProgressInfo>? ScanProgress;

    /// <summary>Raised on the UI thread after a full pass ended: the moment trickplay generation may start.</summary>
    public event EventHandler? ScanSettled;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;
        _started = true;
        // Returns after the saved index was raised and the background scan was queued.
        await _service.StartAsync(ct).ConfigureAwait(true);
        _current ??= _service.Index;
        ApplyStatus(_service.Status);
    }

    /// <summary>Confirms (or changes) the data folder: from now on the library, the scan cache and art are saved there.</summary>
    public void AttachDataFolder(DataFolder folder)
    {
        lock (_lock)
        {
            _data = folder;
            _trick.Clear();
        }
        _service.AttachDataFolder(folder);
    }

    public Task FlushAsync() => _service.FlushAsync();

    // =====================================================================================
    //  Core events (any thread) -> UI thread
    // =====================================================================================

    private void OnIndexChanged(LibraryIndex index, IReadOnlyCollection<string> changed)
    {
        _current = index;
        lock (_lock)
        {
            _pendingFolders ??= new(StringComparer.Ordinal);
            foreach (var f in changed) _pendingFolders.Add(f);
            if (_dispatchQueued) return;
            _dispatchQueued = true;
        }
        _ui.TryEnqueue(() =>
        {
            HashSet<string> folders;
            lock (_lock)
            {
                folders = _pendingFolders ?? [];
                _pendingFolders = null;
                _dispatchQueued = false;
            }
            RefreshMarkersOf(folders);
            IndexChanged?.Invoke(this, new IndexChangedArgs(folders));
        });
    }

    private void OnMediaChanged(string folder, SessionMedia? media) =>
        _ui.TryEnqueue(() => MediaChanged?.Invoke(this, new MediaChangedArgs(folder, media)));

    private void OnStatusChanged(LibraryStatus status) => _ui.TryEnqueue(() => ApplyStatus(status));

    private void ApplyStatus(LibraryStatus s)
    {
        var before = _problem;
        _problem = s.State switch
        {
            LibraryState.NoSteam => "Steam's recording folder wasn't found. Open Settings to point the app at it.",
            LibraryState.RecordingFolderMissing => "Steam's recording folder can't be reached (is the drive connected?). The last known library is shown.",
            _ => null,
        };
        if (before != _problem && _current is not null) IndexChanged?.Invoke(this, new IndexChangedArgs([]));
    }

    private void OnProgress(ScanProgress p) => _ui.TryEnqueue(() =>
    {
        switch (p.Phase)
        {
            case LibraryPhase.Idle:
                if (_scanActive)
                {
                    _scanActive = false;
                    ScanProgress?.Invoke(this, new ScanProgressInfo(false, "Ready"));
                    ScanSettled?.Invoke(this, EventArgs.Empty);
                }
                break;
            case LibraryPhase.Listing:
                _scanActive = true;
                ScanProgress?.Invoke(this, new ScanProgressInfo(true, "Looking for recordings…"));
                break;
            default:
                _scanActive = true;
                var msg = p.Phase == LibraryPhase.Finishing
                    ? "Saving the index…"
                    : $"indexing {p.FoldersDone.ToString(CultureInfo.InvariantCulture)}/{p.FoldersTotal.ToString(CultureInfo.InvariantCulture)}";
                ScanProgress?.Invoke(this, new ScanProgressInfo(true, msg, p.FoldersTotal > 0 ? (double)p.FoldersDone / p.FoldersTotal : null));
                break;
        }
    });

    // =====================================================================================
    //  art, media
    // =====================================================================================

    public string? ResolveArt(string? relativePath)
    {
        var data = Data;
        if (data is null || string.IsNullOrEmpty(relativePath)) return null;
        // The index is data from a shared folder: never let a crafted path leave the data folder.
        var full = Path.GetFullPath(Path.Combine(data.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(data.Root).TrimEnd('\\') + "\\";
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    public SessionMedia? GetMedia(string relativeFolder) => _service.GetMedia(relativeFolder);

    // =====================================================================================
    //  markers (lazy, cached, stale-while-revalidate)
    // =====================================================================================

    public IReadOnlyList<Marker> FullMarkersFor(string relativeFolder)
    {
        lock (_lock)
        {
            if (_markers.TryGetValue(relativeFolder, out var cached)) return cached;
            if (!_markerLoading.Add(relativeFolder)) return [];
        }
        _ = LoadMarkersAsync(relativeFolder);
        return [];
    }

    public IReadOnlyList<MarkerSpot> MarkersFor(RecordingEntry recording)
    {
        var all = FullMarkersFor(recording.RelativeFolder);
        if (all.Count == 0) return [];
        var visible = all.Where(m => MarkerGlyphs.IsSpot(m.Entry));
        if (GetMedia(recording.RelativeFolder) is { } media) visible = Markers.Place(visible, media).Visible;
        return [.. visible.Select(MarkerGlyphs.ToSpot)];
    }

    private async Task LoadMarkersAsync(string folder)
    {
        IReadOnlyList<Marker> list = [];
        try
        {
            var set = await Task.Run(() => _service.LoadMarkersAsync(folder, _cts.Token), _cts.Token).ConfigureAwait(false);
            list = set.Markers;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { /* no markers */ }
        lock (_lock)
        {
            _markers[folder] = list;
            _markerLoading.Remove(folder);
        }
        _ui.TryEnqueue(() => DerivedChanged?.Invoke(this, folder));
    }

    /// <summary>A recording changed on disk: markers already requested for it are loaded again (the old ones stay until then).</summary>
    private void RefreshMarkersOf(IEnumerable<string> changed)
    {
        foreach (var f in changed)
        {
            lock (_lock)
            {
                if (!_markers.ContainsKey(f)) continue;
                if (!_markerLoading.Add(f)) continue;
            }
            _ = LoadMarkersAsync(f);
        }
    }

    // =====================================================================================
    //  trickplay
    // =====================================================================================

    public TrickplayIndex? TrickplayFor(string relativeFolder)
    {
        // The index maps media time through the recording's axis shift (a saved clip's chunk 1 is at its real tfdt, not at 0).
        var shift = GetMedia(relativeFolder)?.AxisShiftSeconds ?? 0;
        DataFolder? data;
        lock (_lock)
        {
            if (_trick.TryGetValue(relativeFolder, out var hit) && hit.AxisShiftSeconds == shift) return hit;
            data = _data;
        }
        var key = TrickplayQueue.KeyOf(relativeFolder);
        if (data is null || !Trickplay.IsSafeKey(key)) return null;
        var idx = TrickplayIndex.TryLoad(data, key, shift);
        if (idx is not null) lock (_lock) _trick[relativeFolder] = idx;
        return idx;
    }

    /// <summary>Called by <see cref="TrickplayQueue"/> after it wrote sheets for a recording (any thread).</summary>
    internal void TrickplayUpdated(string relativeFolder)
    {
        lock (_lock) _trick.Remove(relativeFolder);
        _ui.TryEnqueue(() => DerivedChanged?.Invoke(this, relativeFolder));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _service.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is AggregateException or OperationCanceledException or IOException) { }
        _cts.Dispose();
    }
}
