// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Threading.Channels;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Library;

public enum LibraryState
{
    NotStarted,
    /// <summary>Reading <c>library.json</c> and <c>scan-cache.json</c>.</summary>
    Loading,
    /// <summary>No recording folder is known (Steam not installed or <c>BackgroundRecordPath</c> not set): empty library.</summary>
    NoSteam,
    /// <summary>The recording folder is configured but not reachable (drive or share not mounted). The index is kept as it was, nothing is saved.</summary>
    RecordingFolderMissing,
    /// <summary>A full pass is running.</summary>
    Scanning,
    /// <summary>Scanned and watching.</summary>
    Ready,
    Stopped,
}

public enum LibraryPhase { Idle, Listing, Scanning, Finishing }

/// <param name="WatcherActive">The file system watcher is running (changes arrive as events).</param>
/// <param name="DataFolderConfirmed">Nothing is saved until the data folder is confirmed (<see cref="LibraryService.AttachDataFolderAsync"/>).</param>
/// <param name="Message">A short human-readable detail (the last save problem, why the watcher is off); no paths.</param>
public sealed record LibraryStatus(LibraryState State, bool DataFolderConfirmed, bool WatcherActive, string? Message = null);

/// <summary>Progress of a full pass: folders done of total, which phase, and how many 8-byte head reads it needed so far.</summary>
public sealed record ScanProgress(LibraryPhase Phase, int FoldersDone, int FoldersTotal, int HeadReads);

public sealed record LibraryServiceOptions
{
    /// <summary>Steam's <c>BackgroundRecordPath</c>; null = Steam not found (<see cref="LibraryState.NoSteam"/>).</summary>
    public string? RecordingRoot { get; init; }

    /// <summary>The confirmed data folder; null = not confirmed yet: the library works in memory and nothing is written.</summary>
    public DataFolder? DataFolder { get; init; }

    /// <summary>Game names and art sources; null = names come from <c>library.json</c>, else "App N", and no art.</summary>
    public GameNameResolver? Names { get; init; }

    /// <summary>Default: <see cref="FileSystemFolderWatcher"/>.</summary>
    public Func<IFolderWatcher>? WatcherFactory { get; init; }

    /// <summary>Clock and timers (debounce, save coalescing, live expiry). Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>A folder is rescanned this long after its last event.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A folder that keeps getting events is still rescanned at least this often (below Steam's 3 s chunk period).</summary>
    public TimeSpan MaxDebounce { get; init; } = TimeSpan.FromMilliseconds(2500);

    /// <summary><c>library.json</c> and <c>scan-cache.json</c> are written at most this often.</summary>
    public TimeSpan SaveInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Folders scanned at the same time during a full pass.</summary>
    public int ScanParallelism { get; init; } = 4;

    /// <summary>Minimum time between intermediate index events during a long first pass.</summary>
    public TimeSpan PartialPublishInterval { get; init; } = TimeSpan.FromSeconds(1);

    internal ScanOptions? Scan { get; init; }

    /// <summary>Test seam: called after every successful save.</summary>
    internal Action? Saved { get; init; }
}

/// <summary>
/// The library of recordings, kept current by events only (plan D15). <see cref="StartAsync"/> first raises the index saved in the
/// data folder (nothing is read from the recording folder before that), then scans in the background using the remembered chunk
/// heads, and watches the recording folder. Events may be raised on any thread; keep handlers short (marshal to the UI yourself).
/// Reads Steam's files with Read | Write | Delete sharing and never writes below the recording root.
/// </summary>
public sealed class LibraryService : IAsyncDisposable
{
    private enum WorkKind { Full, Folder, Reassemble }

    private readonly LibraryServiceOptions _o;
    private readonly TimeProvider _clock;
    private readonly string? _root;
    private readonly LibraryBuilder? _builder;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(WorkKind Kind, string? Folder)> _queue = Channel.CreateUnbounded<(WorkKind, string?)>(new() { SingleReader = true });

    private readonly object _lock = new();                                     // state below
    private readonly Dictionary<string, RecordingEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionMedia> _media = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FolderHeads> _heads = new(StringComparer.Ordinal);
    private LibraryIndex _index;
    private LibraryStatus _status;
    private DataFolder? _data;
    private Guid _libraryId;
    private bool _dirty;
    private bool _indexWritable = true;
    private long? _lastSaveStamp;
    private ITimer? _saveTimer;
    private IFolderWatcher? _watcher;

    private readonly object _timerLock = new();                                // debounce and live timers
    private readonly Dictionary<string, (ITimer Timer, long First)> _debounce = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITimer> _live = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _saveLock = new(1);
    private readonly object _idleLock = new();
    private int _pending;
    private TaskCompletionSource _idle = CompletedIdle();
    private int _fullQueued;
    private int _started;
    private volatile bool _disposed;
    private Task _consumer = Task.CompletedTask;

    public LibraryService(LibraryServiceOptions options)
    {
        _o = options;
        _clock = options.Clock ?? TimeProvider.System;
        _root = string.IsNullOrWhiteSpace(options.RecordingRoot) ? null : Path.GetFullPath(options.RecordingRoot);
        _data = options.DataFolder;
        _libraryId = Guid.NewGuid();
        _index = LibraryIndex.Create(_libraryId, _clock.GetUtcNow(), [], []);
        _status = new(LibraryState.NotStarted, _data is not null, false);
        if (_root is not null)
            _builder = new LibraryBuilder(_root, options.Names, _data is null ? null : new ArtworkStore(_data), _clock, options.Scan);
    }

    // ------------------------------------------------------------------------------------------------ public surface

    /// <summary>The current index (empty before <see cref="StartAsync"/>).</summary>
    public LibraryIndex Index { get { lock (_lock) return _index; } }

    public LibraryStatus Status { get { lock (_lock) return _status; } }

    /// <summary>The index changed. <c>changedFolders</c>: relative folders added, changed or removed since the previous event (all folders for the first, loaded-from-disk event; empty when only game names or art changed).</summary>
    public event Action<LibraryIndex, IReadOnlyCollection<string>>? IndexChanged;

    /// <summary>The playable media of one recording folder changed (new chunks, trimmed, live flag), or is gone (null). First argument: <c>RelativeFolder</c>.</summary>
    public event Action<string, SessionMedia?>? MediaChanged;

    public event Action<LibraryStatus>? StatusChanged;
    public event Action<ScanProgress>? ProgressChanged;

    /// <summary>The media of a recording scanned in this session; null when unknown, not yet scanned, or gone.</summary>
    public SessionMedia? GetMedia(string relativeFolder) { lock (_lock) return _media.GetValueOrDefault(relativeFolder); }

    /// <summary>
    /// Loads the saved index and scan cache and raises <see cref="IndexChanged"/> with them (no recording folder access before that),
    /// then starts the watcher and a background scan, and returns. Await <see cref="WhenIdleAsync"/> for the end of the first scan.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("already started");
        SetStatus(LibraryState.Loading);

        LibraryIndex? saved = null;
        var cache = ScanCache.Empty;
        DataFolder? data;
        lock (_lock) data = _data;
        if (data is not null)
        {
            var ip = await new IndexStore(data).ReadAsync(ct).ConfigureAwait(false);
            if (ip.Status is LibraryIndexStatus.Ok or LibraryIndexStatus.NewerSchema) saved = ip.Index;
            _indexWritable = ip.Status != LibraryIndexStatus.NewerSchema;     // never overwrite a newer app's file
            cache = (await new ScanCacheStore(data).ReadAsync(ct).ConfigureAwait(false)).Cache;
        }

        LibraryIndex first;
        lock (_lock)
        {
            if (saved is not null)
            {
                foreach (var r in saved.Recordings) _entries[r.RelativeFolder] = r;
                foreach (var (k, h) in cache.Folders) if (_entries.ContainsKey(k)) _heads[k] = h;
                if (Guid.TryParse(saved.LibraryId, out var sid)) _libraryId = sid;
            }
            if (data?.ReadLibraryId() is { } fid) _libraryId = fid;
            _index = saved is null ? _index with { LibraryId = _libraryId.ToString("D", CultureInfo.InvariantCulture) } : saved;
            first = _index;
        }
        _builder?.Seed(saved);
        Raise(() => IndexChanged?.Invoke(first, [.. first.Recordings.Select(r => r.RelativeFolder)]));

        if (_root is null || _builder is null) { SetStatus(LibraryState.NoSteam); return; }

        _consumer = Task.Run(ConsumeAsync, CancellationToken.None);
        RequestFullRescan();
    }

    /// <summary>Queues a full pass (also restarts the watcher if it died) and completes when the library is idle again.</summary>
    public Task RescanAsync(CancellationToken ct = default)
    {
        RequestFullRescan();
        return WhenIdleAsync(ct);
    }

    /// <summary>Rescans one folder now (no debounce), e.g. when the player opens a live session.</summary>
    public void RequestFolderRescan(string relativeFolder) => Enqueue(WorkKind.Folder, relativeFolder);

    /// <summary>Completes when no scan, rescan or save is queued or running. Debounce timers that have not fired are not work yet.</summary>
    public async Task WhenIdleAsync(CancellationToken ct = default)
    {
        Task t;
        lock (_idleLock) t = _idle.Task;
        await t.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Confirms (or changes) the data folder: from now on the library, the scan cache and art are saved there.</summary>
    public void AttachDataFolder(DataFolder folder)
    {
        lock (_lock)
        {
            _data = folder;
            _dirty = true;
            if (folder.ReadLibraryId() is { } id) _libraryId = id;
        }
        if (_builder is not null)
        {
            _builder.Artwork = new ArtworkStore(folder);
            _builder.ResetArt();
        }
        SetStatus(s => s with { DataFolderConfirmed = true });
        if (_root is not null && Volatile.Read(ref _started) != 0) Enqueue(WorkKind.Reassemble, null);
    }

    /// <summary>Writes the index and scan cache now if anything is unsaved (and a data folder is confirmed).</summary>
    public Task FlushAsync(CancellationToken ct = default) => SaveAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { await _consumer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        IFolderWatcher? w;
        lock (_lock) { w = _watcher; _watcher = null; _saveTimer?.Dispose(); _saveTimer = null; }
        w?.Dispose();
        lock (_timerLock)
        {
            foreach (var (t, _) in _debounce.Values) t.Dispose();
            foreach (var t in _live.Values) t.Dispose();
            _debounce.Clear();
            _live.Clear();
        }
        try { await SaveAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        SetStatus(s => s with { State = LibraryState.Stopped, WatcherActive = false });
        lock (_idleLock) { _pending = 0; _idle.TrySetResult(); }
        _cts.Dispose();
    }

    // ----------------------------------------------------------------------------------------------------- work queue

    private static TaskCompletionSource CompletedIdle()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    private void BeginWork()
    {
        lock (_idleLock)
            if (_pending++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EndWork()
    {
        TaskCompletionSource? done = null;
        lock (_idleLock)
            if (_pending > 0 && --_pending == 0) done = _idle;
        done?.TrySetResult();
    }

    private void Enqueue(WorkKind kind, string? folder)
    {
        if (_disposed) return;
        BeginWork();
        if (!_queue.Writer.TryWrite((kind, folder))) EndWork();
    }

    private void RequestFullRescan()
    {
        if (Interlocked.Exchange(ref _fullQueued, 1) == 0) Enqueue(WorkKind.Full, null);
    }

    private async Task ConsumeAsync()
    {
        var reader = _queue.Reader;
        var ct = _cts.Token;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var items = new List<(WorkKind Kind, string? Folder)>();
                while (reader.TryRead(out var it)) items.Add(it);
                try { await ProcessAsync(items, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    SetStatus(s => s with { Message = "scan failed: " + e.GetType().Name });
                }
                finally { foreach (var _ in items) EndWork(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(List<(WorkKind Kind, string? Folder)> items, CancellationToken ct)
    {
        if (items.Exists(i => i.Kind == WorkKind.Full))
        {
            Interlocked.Exchange(ref _fullQueued, 0);                 // an event during this pass queues the next one
            await FullScanAsync(ct).ConfigureAwait(false);
            return;
        }
        var folders = items.Where(i => i.Kind == WorkKind.Folder).Select(i => i.Folder!).ToHashSet(StringComparer.Ordinal);
        if (folders.Count > 0) await RescanFoldersAsync(folders, ct).ConfigureAwait(false);
        else await PublishAsync(new HashSet<string>(StringComparer.Ordinal), resolveNames: true, ct).ConfigureAwait(false);
    }

    // --------------------------------------------------------------------------------------------------- scanning

    private async Task FullScanAsync(CancellationToken ct)
    {
        var b = _builder!;
        if (!Directory.Exists(b.Root))
        {
            SetStatus(s => s with { State = LibraryState.RecordingFolderMissing, WatcherActive = false });
            Progress(LibraryPhase.Idle, 0, 0, 0);
            return;
        }
        SetStatus(s => s with { State = LibraryState.Scanning });
        Progress(LibraryPhase.Listing, 0, 0, 0);
        EnsureWatcher();                                              // before the listing: nothing can slip through the gap

        var folders = b.Enumerate();
        if (!Directory.Exists(b.Root)) { SetStatus(s => s with { State = LibraryState.RecordingFolderMissing }); return; }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        var gone = new List<string>();
        lock (_lock)
        {
            var onDisk = folders.Select(f => f.RelativeFolder).ToHashSet(StringComparer.Ordinal);
            foreach (var k in _entries.Keys.Where(k => !onDisk.Contains(k)).ToList()) { gone.Add(k); _entries.Remove(k); }
            foreach (var k in _media.Keys.Where(k => !onDisk.Contains(k)).ToList()) { _media.Remove(k); if (!gone.Contains(k)) gone.Add(k); }
            foreach (var k in _heads.Keys.Where(k => !onDisk.Contains(k)).ToList()) { _heads.Remove(k); _dirty = true; }
            if (gone.Count > 0) _dirty = true;
        }
        foreach (var k in gone) { changed.Add(k); DropLive(k); Raise(() => MediaChanged?.Invoke(k, null)); }

        var ctx = await b.LoadContextAsync(ct).ConfigureAwait(false);
        int done = 0, reads = 0, total = folders.Count;
        var lastPartial = _clock.GetTimestamp();
        Progress(LibraryPhase.Scanning, 0, total, 0);

        await b.ScanFoldersAsync(folders, k => { lock (_lock) return _heads.GetValueOrDefault(k); }, ctx, _o.ScanParallelism, async s =>
        {
            CommitAndNotify(s, changed);
            done++;
            reads += s.HeadReads;
            if (done == total || _clock.GetElapsedTime(lastPartial) >= _o.PartialPublishInterval)
            {
                Progress(LibraryPhase.Scanning, done, total, reads);
                if (done < total && changed.Count > 0)
                {
                    lastPartial = _clock.GetTimestamp();
                    await PublishAsync(changed, resolveNames: false, ct).ConfigureAwait(false);   // the library fills while a cold scan runs
                }
            }
        }, ct).ConfigureAwait(false);

        Progress(LibraryPhase.Finishing, done, total, reads);
        await PublishAsync(changed, resolveNames: true, ct).ConfigureAwait(false);
        SetStatus(s => s with { State = LibraryState.Ready });
        Progress(LibraryPhase.Idle, done, total, reads);
    }

    private async Task RescanFoldersAsync(HashSet<string> relative, CancellationToken ct)
    {
        var b = _builder!;
        var list = relative.Select(b.FolderFor).OfType<RecordingFolder>().ToList();
        if (list.Count == 0) return;
        var ctx = await b.LoadContextAsync(ct).ConfigureAwait(false);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        await b.ScanFoldersAsync(list, k => { lock (_lock) return _heads.GetValueOrDefault(k); }, ctx, _o.ScanParallelism,
            s => { CommitAndNotify(s, changed); return ValueTask.CompletedTask; }, ct).ConfigureAwait(false);
        await PublishAsync(changed, resolveNames: true, ct).ConfigureAwait(false);
    }

    /// <summary>Applies one folder result to the state, raises <see cref="MediaChanged"/> if its media changed, and arms the live-expiry timer.</summary>
    private void CommitAndNotify(FolderScan s, HashSet<string> changed)
    {
        if (s.Failed) return;                                         // keep what we knew
        var key = s.Folder.RelativeFolder;
        SessionMedia? raiseMedia = null;
        var mediaChanged = false;
        lock (_lock)
        {
            _entries.TryGetValue(key, out var prevEntry);
            _media.TryGetValue(key, out var prevMedia);
            if (s.Result.Status == ScanStatus.Playable && s.Entry is { } e && s.Result.Media is { } m)
            {
                _entries[key] = e;
                _media[key] = m;
                if (!_heads.TryGetValue(key, out var h) || !h.SameAs(s.Heads)) { _heads[key] = s.Heads; _dirty = true; }
                var entryChanged = prevEntry is null || !SameEntry(prevEntry, e);
                if (entryChanged) changed.Add(key);
                mediaChanged = prevMedia is null ? entryChanged : !SameMedia(prevMedia, m);
                raiseMedia = m;
            }
            else
            {
                var existed = _entries.Remove(key);
                _media.Remove(key);
                if (s.Result.Status == ScanStatus.Gone) { if (_heads.Remove(key)) _dirty = true; }
                else if (!_heads.TryGetValue(key, out var h) || !h.SameAs(s.Heads)) { _heads[key] = s.Heads; _dirty = true; }
                if (existed) changed.Add(key);
                mediaChanged = existed || prevMedia is not null;
            }
        }

        if (raiseMedia is { Live: true }) ArmLive(key, s.Result.NewestChunkUtc);
        else DropLive(key);
        if (mediaChanged) { var m = raiseMedia; Raise(() => MediaChanged?.Invoke(key, m)); }
    }

    /// <summary>Assembles the index; raises <see cref="IndexChanged"/> (and consumes <paramref name="changed"/>) if it differs; schedules a save.</summary>
    private async Task PublishAsync(HashSet<string> changed, bool resolveNames, CancellationToken ct)
    {
        List<RecordingEntry> snapshot;
        Guid id;
        lock (_lock) { snapshot = [.. _entries.Values]; id = _libraryId; }
        var index = await _builder!.AssembleAsync(id, snapshot, resolveNames, ct).ConfigureAwait(false);
        var raise = false;
        lock (_lock)
        {
            if (!IndexEquals(_index, index)) { _index = index; _dirty = true; raise = true; }
        }
        if (raise)
        {
            var batch = changed.ToArray();
            changed.Clear();
            Raise(() => IndexChanged?.Invoke(index, batch));
        }
        await MaybeSaveAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------------ watcher

    private void EnsureWatcher()
    {
        lock (_lock) if (_watcher is not null) return;
        IFolderWatcher? w = null;
        try
        {
            w = (_o.WatcherFactory ?? (static () => new FileSystemFolderWatcher()))();
            w.Changed += OnChange;
            w.Failed += OnWatcherFailed;
            w.Start(_root!);
            lock (_lock) _watcher = w;
            SetStatus(s => s with { WatcherActive = true });
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            w?.Dispose();
            SetStatus(s => s with { WatcherActive = false, Message = "watcher unavailable: " + e.GetType().Name });
        }
    }

    private void OnWatcherFailed(Exception e)
    {
        if (_disposed) return;
        if (e is not InternalBufferOverflowException)
        {
            // The watch is dead (folder or share went away): drop it, the full pass below starts a new one.
            IFolderWatcher? w;
            lock (_lock) { w = _watcher; _watcher = null; }
            w?.Dispose();
            SetStatus(s => s with { WatcherActive = false });
        }
        RequestFullRescan();                                          // events may have been lost: look at everything
    }

    private void OnChange(FolderChange c)
    {
        if (_disposed || _root is null) return;
        try
        {
            Handle(c.Kind, c.FullPath);
            if (c.OldFullPath is { } old) Handle(FolderChangeKind.Deleted, old);
        }
        catch (Exception e) when (e is ArgumentException or IOException) { /* a path we cannot interpret is not ours */ }
    }

    private void Handle(FolderChangeKind kind, string path)
    {
        var rel = Path.GetRelativePath(_root!, path);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return;
        var seg = rel.Split('\\', '/');
        var top = seg[0].ToLowerInvariant();
        switch (top)
        {
            case "video" or "clips":
                if (seg.Length == 1) { if (kind != FolderChangeKind.Changed) RequestFullRescan(); return; }
                var key = top + "/" + seg[1];
                if (_builder!.FolderFor(key) is null) return;         // not a recording folder name: ignored, never guessed
                if (kind == FolderChangeKind.Changed && (seg.Length == 2 || seg[^1].EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))) return;
                ArmFolder(key);
                break;
            case "timelines":
                if (seg.Length == 1) { if (kind != FolderChangeKind.Changed) RequestFullRescan(); return; }
                if (FolderNames.ParseTimeline(seg[1]) is { } tn) ArmGame(tn.GameId.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    private void ArmGame(string gameId)
    {
        string[] keys;
        lock (_lock)
            keys = [.. _entries.Where(e => e.Value.Kind == RecordingKind.CacheSession && e.Value.GameId == gameId).Select(e => e.Key)];
        foreach (var k in keys) ArmFolder(k);
    }

    /// <summary>Trailing debounce per folder with a cap: a folder that is written continuously is still rescanned every <see cref="LibraryServiceOptions.MaxDebounce"/>.</summary>
    private void ArmFolder(string key)
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            if (_debounce.TryGetValue(key, out var d))
            {
                if (_clock.GetElapsedTime(d.First) < _o.MaxDebounce) d.Timer.Change(_o.Debounce, Timeout.InfiniteTimeSpan);
                return;
            }
            var first = _clock.GetTimestamp();
            _debounce[key] = (_clock.CreateTimer(_ => FireFolder(key), null, _o.Debounce, Timeout.InfiniteTimeSpan), first);
        }
    }

    private void FireFolder(string key)
    {
        lock (_timerLock)
            if (_debounce.Remove(key, out var d)) d.Timer.Dispose();
        Enqueue(WorkKind.Folder, key);
    }

    /// <summary>One-shot timer for the moment a live session stops being "live" (no chunk for <see cref="ScanAnalysis.RecentThreshold"/>): no polling, no event would tell us.</summary>
    private void ArmLive(string key, DateTimeOffset? newestChunk)
    {
        var due = (newestChunk ?? _clock.GetUtcNow()) + ScanAnalysis.RecentThreshold + TimeSpan.FromSeconds(1) - _clock.GetUtcNow();
        if (due < TimeSpan.FromSeconds(1)) due = TimeSpan.FromSeconds(1);
        lock (_timerLock)
        {
            if (_disposed) return;
            if (_live.Remove(key, out var old)) old.Dispose();
            _live[key] = _clock.CreateTimer(_ =>
            {
                lock (_timerLock) if (_live.Remove(key, out var t)) t.Dispose();
                Enqueue(WorkKind.Folder, key);
            }, null, due, Timeout.InfiniteTimeSpan);
        }
    }

    private void DropLive(string key)
    {
        lock (_timerLock)
            if (_live.Remove(key, out var t)) t.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------- saving

    /// <summary>Saves now if the last save is older than the interval, otherwise arms a one-shot timer for the remainder.</summary>
    private async Task MaybeSaveAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_lock)
        {
            if (_data is null || !_dirty) return;
            wait = _lastSaveStamp is { } s ? _o.SaveInterval - _clock.GetElapsedTime(s) : TimeSpan.Zero;
            if (wait > TimeSpan.Zero)
            {
                if (_saveTimer is null && !_disposed)
                    _saveTimer = _clock.CreateTimer(_ => FireSave(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }
        }
        await SaveAsync(ct).ConfigureAwait(false);
    }

    private void FireSave()
    {
        lock (_lock) { _saveTimer?.Dispose(); _saveTimer = null; }
        if (_disposed) return;
        BeginWork();
        _ = Task.Run(async () =>
        {
            try { await SaveAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { EndWork(); }
        });
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DataFolder? data;
            LibraryIndex index;
            ScanCache cache;
            bool writeIndex;
            lock (_lock)
            {
                if (_data is null || !_dirty) return;
                data = _data;
                _dirty = false;
                index = _index;
                cache = ScanCache.From(_heads);
                writeIndex = _indexWritable;
            }
            try
            {
                var id = data.ReadOrCreateLibraryId();
                lock (_lock) _libraryId = id;
                if (writeIndex)
                    await new IndexStore(data).WriteAsync(index with { LibraryId = id.ToString("D", CultureInfo.InvariantCulture), GeneratedUtc = _clock.GetUtcNow() }, ct).ConfigureAwait(false);
                await new ScanCacheStore(data).WriteAsync(cache, ct).ConfigureAwait(false);
                lock (_lock) _lastSaveStamp = _clock.GetTimestamp();
                _o.Saved?.Invoke();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                lock (_lock) _dirty = true;                           // try again with the next change
                SetStatus(s => s with { Message = "saving failed: " + e.GetType().Name });
            }
        }
        finally { _saveLock.Release(); }
    }

    // --------------------------------------------------------------------------------------------------- helpers

    private void SetStatus(LibraryState state) => SetStatus(s => s with { State = state });

    private void SetStatus(Func<LibraryStatus, LibraryStatus> change)
    {
        LibraryStatus next;
        lock (_lock)
        {
            next = change(_status);
            if (next == _status) return;
            _status = next;
        }
        Raise(() => StatusChanged?.Invoke(next));
    }

    private void Progress(LibraryPhase phase, int done, int total, int reads) =>
        Raise(() => ProgressChanged?.Invoke(new(phase, done, total, reads)));

    private static void Raise(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { /* a subscriber must not stop the library */ }
    }

    internal static bool SameEntry(RecordingEntry a, RecordingEntry b) =>
        RunsEqual(a.Runs, b.Runs) && a with { Runs = b.Runs } == b;

    internal static bool SameMedia(SessionMedia a, SessionMedia b) =>
        a.Folder == b.Folder && a.SegmentSeconds == b.SegmentSeconds && a.Live == b.Live && a.Temporary == b.Temporary && RunsEqual(a.Runs, b.Runs);

    private static bool RunsEqual(IReadOnlyList<SegmentRun> a, IReadOnlyList<SegmentRun> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Same games and recordings; ignores <c>GeneratedUtc</c> and the library id.</summary>
    internal static bool IndexEquals(LibraryIndex a, LibraryIndex b)
    {
        if (a.Games.Count != b.Games.Count || a.Recordings.Count != b.Recordings.Count) return false;
        for (var i = 0; i < a.Games.Count; i++) if (a.Games[i] != b.Games[i]) return false;
        for (var i = 0; i < a.Recordings.Count; i++) if (!SameEntry(a.Recordings[i], b.Recordings[i])) return false;
        return true;
    }
}
