// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Net;

/// <summary>Read-only access to the remote recording files. <see cref="SharedFolderFiles"/> is the real one.</summary>
public interface IRemoteFiles
{
    /// <summary>The file's size, or null when it is gone (Steam trimmed it) while its folder is still reachable.</summary>
    /// <exception cref="IOException">The folder can't be reached.</exception>
    ValueTask<long?> SizeAsync(string path, CancellationToken ct);

    /// <exception cref="IOException">The file can't be read now.</exception>
    Stream Open(string path);
}

/// <summary>A library reached through a path, typically a UNC share. Files are opened read-only and never block Steam's cleanup.</summary>
public sealed class SharedFolderFiles : IRemoteFiles
{
    public async ValueTask<long?> SizeAsync(string path, CancellationToken ct) =>
        await Task.Run<long?>(() =>
        {
            var info = new FileInfo(path);
            if (info.Exists) return info.Length;
            if (!Directory.Exists(info.DirectoryName)) throw new IOException("The remote folder can't be reached.");
            return null;
        }, ct).ConfigureAwait(false);

    public Stream Open(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
}

/// <summary>What to fetch: the init segments and <paramref name="Chunks"/> of one remote recording.</summary>
/// <param name="Recording">The recording's plain folder name; the cache key.</param>
public sealed record DownloadRequest(string LibraryId, string Recording, SessionMedia RemoteMedia, IReadOnlyList<int> Chunks);

public enum DownloadState { Running, Reconnecting, Done, CannotReach, Cancelled }

/// <param name="Missing">Files Steam removed before they were fetched: they stay gaps.</param>
/// <param name="Bytes">Bytes fetched by this run.</param>
public sealed record DownloadProgress(int Done, int Total, int Missing, long Bytes, DownloadState State);

/// <summary>
/// Download-first transfer of Steam's chunk files into <see cref="LocalChunkCache"/> (plan §4.3). A chunk is immutable once complete, so
/// each file is fetched once; the cache writes <c>.part</c>, checks the size and renames, so a dropped connection loses at most the
/// files in flight and a run started later skips everything already complete. Fetch order follows the playhead (<see cref="Seek"/>).
/// Disconnects follow <see cref="ReconnectPolicy"/>: no polling, the workers sleep until the next attempt or a network change.
/// </summary>
public sealed class ChunkDownloader(LocalChunkCache cache, IRemoteFiles files, TimeProvider? time = null, int parallel = 2)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private ReconnectPolicy _policy = new(TimeProvider.System, Random.Shared);
    private TaskCompletionSource _networkChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<ChunkKey> _inits = [];
    private Dictionary<int, List<ChunkKey>> _chunks = [];
    private int _playhead = int.MinValue;

    /// <summary>Fetch from this chunk on first. Safe at any time.</summary>
    public void Seek(int playheadChunk)
    {
        lock (_gate) _playhead = playheadChunk;
    }

    /// <summary>A network came up or changed: a reconnecting run retries at once.</summary>
    public void NetworkChanged()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            _policy.OnNetworkChanged();
            old = _networkChanged;
            _networkChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    /// <summary>Runs until every file is cached or gone, the server can't be reached (<see cref="DownloadState.CannotReach"/>), or it is cancelled.</summary>
    public async Task<DownloadProgress> RunAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var inits = new List<ChunkKey>();
        var chunks = new Dictionary<int, List<ChunkKey>>();
        var total = 0;
        var done = 0;
        foreach (var key in Keys(request))
        {
            total++;
            if (cache.Contains(key)) { done++; continue; }
            if (key.Chunk == 0) inits.Add(key);
            else if (chunks.TryGetValue(key.Chunk, out var list)) list.Add(key);
            else chunks[key.Chunk] = [key];
        }
        var run = new Run(request, progress, total, done);
        lock (_gate)
        {
            _policy = new ReconnectPolicy(_time, Random.Shared);
            _inits = inits;
            _chunks = chunks;
            if (_playhead == int.MinValue) _playhead = 0;
        }
        progress?.Report(run.Snapshot(DownloadState.Running));

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, parallel)).Select(_ => Task.Run(() => WorkerAsync(run, ct), ct))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Finish(run, DownloadState.Cancelled);
        }
        return Finish(run, run.CannotReach ? DownloadState.CannotReach : DownloadState.Done);
    }

    private DownloadProgress Finish(Run run, DownloadState state)
    {
        lock (_gate) _playhead = int.MinValue;
        var last = run.Snapshot(state);
        run.Progress?.Report(last);
        return last;
    }

    private static IEnumerable<ChunkKey> Keys(DownloadRequest r)
    {
        for (var stream = 0; stream < 2; stream++) yield return new ChunkKey(r.LibraryId, r.Recording, stream, 0);
        foreach (var chunk in r.Chunks.Distinct())
            for (var stream = 0; stream < 2; stream++) yield return new ChunkKey(r.LibraryId, r.Recording, stream, chunk);
    }

    private static string RemotePath(SessionMedia media, ChunkKey key) =>
        Path.Combine(media.Folder, key.Chunk == 0 ? FolderNames.InitFile(key.Stream) : FolderNames.ChunkFile(key.Stream, key.Chunk));

    private async Task WorkerAsync(Run run, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!await WaitForLinkAsync(run, ct).ConfigureAwait(false)) return;
            ChunkKey key;
            lock (_gate)
            {
                if (!TryTake(out key)) return;
            }
            try
            {
                var size = await files.SizeAsync(RemotePath(run.Request.RemoteMedia, key), ct).ConfigureAwait(false);
                if (size is { } length)
                {
                    await using var source = files.Open(RemotePath(run.Request.RemoteMedia, key));
                    await cache.PutAsync(key, source, length, ct).ConfigureAwait(false);
                    run.Fetched(length);
                }
                else
                {
                    run.Gone();
                }
                lock (_gate) _policy.OnConnected();
                run.Progress?.Report(run.Snapshot(DownloadState.Running));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                lock (_gate)
                {
                    PutBack(key);
                    _policy.OnDisconnected();
                    _policy.OnAttemptFailed();
                }
                run.Progress?.Report(run.Snapshot(DownloadState.Reconnecting));
            }
        }
    }

    /// <summary>Waits while reconnecting. False when the server timed out: this run is over and a later one resumes it.</summary>
    private async Task<bool> WaitForLinkAsync(Run run, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            Task changed;
            lock (_gate)
            {
                var state = _policy.State;
                if (state == ReconnectState.TimedOut) { run.CannotReach = true; return false; }
                if (state == ReconnectState.Connected) return true;
                wait = _policy.TimeUntilNextAttempt;
                if (wait <= TimeSpan.Zero) return true;
                changed = _networkChanged.Task;
            }
            await Task.WhenAny(Task.Delay(wait, _time, ct), changed).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    private bool TryTake(out ChunkKey key)
    {
        if (_inits.Count > 0)
        {
            key = _inits[0];
            _inits.RemoveAt(0);
            return true;
        }
        var order = ChunkPlan.Order(_chunks.Keys, _playhead, _ => false);
        if (order.Count == 0) { key = default; return false; }
        var list = _chunks[order[0]];
        key = list[0];
        list.RemoveAt(0);
        if (list.Count == 0) _chunks.Remove(order[0]);
        return true;
    }

    private void PutBack(ChunkKey key)
    {
        if (key.Chunk == 0) _inits.Insert(0, key);
        else if (_chunks.TryGetValue(key.Chunk, out var list)) list.Insert(0, key);
        else _chunks[key.Chunk] = [key];
    }

    private sealed class Run(DownloadRequest request, IProgress<DownloadProgress>? progress, int total, int done)
    {
        private int _done = done;
        private int _missing;
        private long _bytes;

        public DownloadRequest Request { get; } = request;
        public IProgress<DownloadProgress>? Progress { get; } = progress;
        public volatile bool CannotReach;

        public void Fetched(long bytes)
        {
            Interlocked.Increment(ref _done);
            Interlocked.Add(ref _bytes, bytes);
        }

        public void Gone()
        {
            Interlocked.Increment(ref _done);
            Interlocked.Increment(ref _missing);
        }

        public DownloadProgress Snapshot(DownloadState state) =>
            new(Volatile.Read(ref _done), total, Volatile.Read(ref _missing), Interlocked.Read(ref _bytes), state);
    }
}
