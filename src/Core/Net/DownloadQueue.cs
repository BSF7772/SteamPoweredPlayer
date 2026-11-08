// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Net;

public enum DownloadItemState { Waiting, Running, Done, CannotReach, Cancelled, Failed }

/// <summary>A queued download. Mutated only by <see cref="DownloadQueue"/>.</summary>
public sealed class DownloadItem(string libraryId, RemoteRecording recording)
{
    internal CancellationTokenSource? Cts;

    public string LibraryId { get; } = libraryId;
    public RemoteRecording Recording { get; } = recording;
    public DownloadItemState State { get; internal set; }
    public DownloadProgress? Progress { get; internal set; }
    public string? Error { get; internal set; }

    public double Fraction => Progress is { Total: > 0 } p ? (double)p.Done / p.Total : 0;
}

/// <summary>
/// One recording at a time, in order, independent of any page; since each recording has its own keys, this also means one writer per
/// chunk key. A recording the server dropped waits for a network change or a retry and then continues from the cache.
/// <see cref="Changed"/> fires on a worker thread.
/// </summary>
public sealed class DownloadQueue(LocalChunkCache cache, IRemoteFiles files, TimeProvider? time = null)
{
    private readonly ChunkDownloader _downloader = new(cache, files, time);
    private readonly List<DownloadItem> _items = [];
    private readonly object _gate = new();
    private Task _pump = Task.CompletedTask;
    private bool _pumping;

    public event Action<DownloadItem>? Changed;

    public IReadOnlyList<DownloadItem> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    public int Pending
    {
        get { lock (_gate) return _items.Count(i => i.State is DownloadItemState.Waiting or DownloadItemState.Running); }
    }

    /// <summary>Adds a recording, or returns its item when it is already queued or running; a finished one is queued again for what is left.</summary>
    public DownloadItem Enqueue(string libraryId, RemoteRecording recording)
    {
        DownloadItem item;
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(i => i.LibraryId == libraryId && i.Recording.Folder == recording.Folder);
            if (existing is { State: DownloadItemState.Waiting or DownloadItemState.Running }) return existing;
            if (existing is not null) _items.Remove(existing);
            item = new DownloadItem(libraryId, recording);
            _items.Add(item);
            StartPump();
        }
        Changed?.Invoke(item);
        return item;
    }

    public void Cancel(DownloadItem item)
    {
        CancellationTokenSource? cts;
        var waiting = false;
        lock (_gate)
        {
            if (item.State is not (DownloadItemState.Waiting or DownloadItemState.Running or DownloadItemState.CannotReach)) return;
            cts = item.Cts;
            if (item.State != DownloadItemState.Running)
            {
                item.State = DownloadItemState.Cancelled;
                waiting = true;
            }
        }
        cts?.Cancel();
        if (waiting) Changed?.Invoke(item);
    }

    /// <summary>A network came up or changed: a reconnecting download retries now, and those that gave up start again.</summary>
    public void NetworkChanged()
    {
        _downloader.NetworkChanged();
        List<DownloadItem> resumed;
        lock (_gate)
        {
            resumed = [.. _items.Where(i => i.State == DownloadItemState.CannotReach)];
            foreach (var item in resumed) item.State = DownloadItemState.Waiting;
            if (resumed.Count > 0) StartPump();
        }
        foreach (var item in resumed) Changed?.Invoke(item);
    }

    /// <summary>Fetch this recording's chunks from <paramref name="chunk"/> on first.</summary>
    public void Seek(int chunk) => _downloader.Seek(chunk);

    /// <summary>Completes when the queue has run dry (tests).</summary>
    public Task IdleAsync()
    {
        lock (_gate) return _pump;
    }

    private void StartPump()
    {
        if (_pumping) return;
        _pumping = true;
        _pump = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            DownloadItem? item;
            lock (_gate)
            {
                item = _items.FirstOrDefault(i => i.State == DownloadItemState.Waiting);
                if (item is null) { _pumping = false; return; }
                item.State = DownloadItemState.Running;
                item.Cts = new CancellationTokenSource();
            }
            Changed?.Invoke(item);
            await RunOneAsync(item).ConfigureAwait(false);
            Changed?.Invoke(item);
        }
    }

    private async Task RunOneAsync(DownloadItem item)
    {
        try
        {
            var media = item.Recording.Media;
            var request = new DownloadRequest(item.LibraryId, item.Recording.Folder, media, [.. media.Chunks()]);
            var result = await _downloader.RunAsync(request, new Inline(p =>
            {
                item.Progress = p;
                Changed?.Invoke(item);
            }), item.Cts!.Token).ConfigureAwait(false);
            await cache.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                item.Progress = result;
                item.State = result.State switch
                {
                    DownloadState.Done => DownloadItemState.Done,
                    DownloadState.CannotReach => DownloadItemState.CannotReach,
                    _ => DownloadItemState.Cancelled,
                };
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)             // disk full, bad name: this item's result, not the queue's end
        {
            lock (_gate)
            {
                item.Error = e.Message;
                item.State = DownloadItemState.Failed;
            }
        }
    }

    private sealed class Inline(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
