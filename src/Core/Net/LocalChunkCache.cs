// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Net;

/// <summary>
/// Identifies one downloaded file: <c>Chunk</c> ≥ 1 is <c>chunk-stream{Stream}-NNNNN.m4s</c>, <c>Chunk == 0</c> is
/// <c>init-stream{Stream}.m4s</c>. <see cref="LibraryId"/> and <see cref="Recording"/> become folder names, so they must be plain
/// names (letters, digits, <c>._-</c>; never <c>.</c> or <c>..</c>).
/// </summary>
public readonly record struct ChunkKey(string LibraryId, string Recording, int Stream, int Chunk);

/// <summary>
/// The app's OWN on-disk cache of chunks downloaded from remote libraries (never Steam's folder). Layout:
/// <c>&lt;dir&gt;/&lt;libraryId&gt;/&lt;recording&gt;/&lt;Steam file name&gt;</c>, so cached files can be handed to the player as they are.
/// <list type="bullet">
/// <item>Writes go to <c>&lt;name&gt;.part</c>, are size-checked against the expected size and renamed atomically; a leftover
/// <c>.part</c> is never listed and is deleted when the cache is opened.</item>
/// <item>LRU eviction down to a byte cap. Never evicts chunks of pinned ("Keep offline") recordings, chunks leased by playback
/// (<see cref="MarkInUse"/>) or the entry that was just written; the cache may therefore exceed its cap.</item>
/// <item>The files on disk are the truth; <c>cache-index.json</c> only carries the recency order and the pins, and is replaced
/// atomically (temp file + rename). A missing or corrupt one costs the order and the pins, nothing else.</item>
/// </list>
/// This is the only place that deletes cache files (eviction, <c>.part</c> cleanup); it never touches anything outside <c>dir</c>.
/// Thread-safe.
/// </summary>
public sealed partial class LocalChunkCache
{
    private const string IndexFileName = "cache-index.json";
    private const string PartSuffix = ".part";

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNameRegex();

    [GeneratedRegex(@"^init-stream([01])\.m4s$", RegexOptions.CultureInvariant)]
    private static partial Regex InitRegex();

    private sealed record Entry(long Size, long Seq);

    private sealed class IndexDto
    {
        public long Clock { get; set; }
        public List<EntryDto> Entries { get; set; } = [];
        public List<PinDto> Pinned { get; set; } = [];
    }

    private sealed class EntryDto
    {
        public string Library { get; set; } = "";
        public string Recording { get; set; } = "";
        public int Stream { get; set; }
        public int Chunk { get; set; }
        public long Seq { get; set; }
    }

    private sealed class PinDto
    {
        public string Library { get; set; } = "";
        public string Recording { get; set; } = "";
    }

    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly Dictionary<ChunkKey, Entry> _entries = [];
    private readonly HashSet<(string Library, string Recording)> _pinned = [];
    private readonly Dictionary<ChunkKey, int> _inUse = [];
    private long _clock;
    private long _total;

    private LocalChunkCache(string dir, long maxBytes)
    {
        _dir = dir;
        _maxBytes = maxBytes;
    }

    /// <summary>Opens (creating if needed) the cache at <paramref name="dir"/>: removes leftover <c>.part</c> files, loads the
    /// bookkeeping, scans the files and trims to <paramref name="maxBytes"/>.</summary>
    public static async Task<LocalChunkCache> OpenAsync(string dir, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var cache = new LocalChunkCache(Path.GetFullPath(dir), maxBytes);
        Directory.CreateDirectory(cache._dir);
        var index = await cache.LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() => cache.Scan(index, cancellationToken), cancellationToken).ConfigureAwait(false);
        bool trimmed;
        lock (cache._gate) trimmed = cache.EvictLocked(null);
        if (trimmed || index is null) await cache.SaveIndexAsync(cancellationToken).ConfigureAwait(false);
        return cache;
    }

    public long MaxBytes => _maxBytes;

    /// <summary>Bytes of all complete cached files.</summary>
    public long TotalBytes
    {
        get { lock (_gate) return _total; }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Whether the file is cached (does not count as a use for LRU).</summary>
    public bool Contains(ChunkKey key)
    {
        Validate(key);
        lock (_gate) return _entries.ContainsKey(key);
    }

    /// <summary>The path of a complete cached file, or null. Counts as a use (moves it to the young end of the LRU order).
    /// A file that vanished behind the cache's back is forgotten and reported as missing.</summary>
    public string? TryGetPath(ChunkKey key)
    {
        Validate(key);
        var path = PathOf(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e)) return null;
            if (!File.Exists(path))
            {
                _entries.Remove(key);
                _total -= e.Size;
                return null;
            }
            _entries[key] = e with { Seq = ++_clock };
            return path;
        }
    }

    /// <summary>
    /// Stores <paramref name="content"/> as the file for <paramref name="key"/>: <c>.part</c> → size check → atomic rename, then
    /// evicts down to the cap. Reads at most <paramref name="expectedSize"/> + 1 bytes. Replaces an existing entry.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream did not deliver exactly <paramref name="expectedSize"/> bytes.</exception>
    public async Task<string> PutAsync(ChunkKey key, Stream content, long expectedSize, CancellationToken cancellationToken = default)
    {
        Validate(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSize);
        cancellationToken.ThrowIfCancellationRequested();

        var path = PathOf(key);
        var part = path + PartSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long written = 0;
        try
        {
            var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (fs.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                while (written <= expectedSize)
                {
                    var want = (int)Math.Min(buffer.Length, expectedSize + 1 - written);   // one byte past the end exposes oversize
                    var n = await content.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                    if (n == 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    written += n;
                }
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (written != expectedSize)
                throw new InvalidDataException($"Expected {expectedSize} bytes for {Path.GetFileName(path)}, got {(written > expectedSize ? "more" : written.ToString(System.Globalization.CultureInfo.InvariantCulture))}.");

            lock (_gate)
            {
                File.Move(part, path, overwrite: true);                 // atomic: complete or absent, never partial
                if (_entries.TryGetValue(key, out var old)) _total -= old.Size;
                _entries[key] = new Entry(expectedSize, ++_clock);
                _total += expectedSize;
                EvictLocked(key);
            }
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>Keeps every chunk of the recording ("Keep offline") out of eviction. Persisted.</summary>
    public Task PinAsync(string libraryId, string recording, CancellationToken cancellationToken = default)
    {
        ValidateName(libraryId);
        ValidateName(recording);
        lock (_gate) _pinned.Add((libraryId, recording));
        return SaveIndexAsync(cancellationToken);
    }

    public async Task UnpinAsync(string libraryId, string recording, CancellationToken cancellationToken = default)
    {
        ValidateName(libraryId);
        ValidateName(recording);
        bool removed;
        lock (_gate) removed = _pinned.Remove((libraryId, recording));
        if (removed) await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool IsPinned(string libraryId, string recording)
    {
        lock (_gate) return _pinned.Contains((libraryId, recording));
    }

    /// <summary>Marks the chunk as being played or read: it is not evicted until the returned lease is disposed (idempotent).
    /// Leases are counted and not persisted.</summary>
    public IDisposable MarkInUse(ChunkKey key)
    {
        Validate(key);
        lock (_gate) _inUse[key] = _inUse.GetValueOrDefault(key) + 1;
        return new Lease(this, key);
    }

    /// <summary>Writes the bookkeeping now (LRU touches from <see cref="TryGetPath"/> are otherwise saved with the next write).</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => SaveIndexAsync(cancellationToken);

    private sealed class Lease(LocalChunkCache owner, ChunkKey key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._gate)
            {
                var n = owner._inUse.GetValueOrDefault(key) - 1;
                if (n <= 0) owner._inUse.Remove(key); else owner._inUse[key] = n;
            }
        }
    }

    /// <summary>Deletes least-recently-used evictable files until within the cap. Caller holds the lock. Returns whether any went.</summary>
    private bool EvictLocked(ChunkKey? keep)
    {
        var any = false;
        HashSet<ChunkKey> undeletable = [];                              // held open elsewhere: skipped for the rest of this pass
        while (_total > _maxBytes)
        {
            ChunkKey? victim = null;
            var oldest = long.MaxValue;
            foreach (var (k, e) in _entries)
            {
                if (k == keep || e.Seq >= oldest || undeletable.Contains(k)) continue;
                if (_pinned.Contains((k.LibraryId, k.Recording)) || _inUse.ContainsKey(k)) continue;
                victim = k;
                oldest = e.Seq;
            }
            if (victim is not { } v) break;
            var size = _entries[v].Size;
            try
            {
                File.Delete(PathOf(v));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                undeletable.Add(v);
                continue;
            }
            _entries.Remove(v);
            _total -= size;
            any = true;
        }
        return any;
    }

    private void Scan(IndexDto? index, CancellationToken ct)
    {
        var seqs = new Dictionary<ChunkKey, long>();
        if (index is not null)
        {
            _clock = index.Clock;
            foreach (var e in index.Entries) seqs[new ChunkKey(e.Library, e.Recording, e.Stream, e.Chunk)] = e.Seq;
            foreach (var p in index.Pinned)
                if (IsPlain(p.Library) && IsPlain(p.Recording)) _pinned.Add((p.Library, p.Recording));
        }

        foreach (var libDir in Directory.EnumerateDirectories(_dir))
        {
            var lib = Path.GetFileName(libDir);
            if (!IsPlain(lib)) continue;
            foreach (var recDir in Directory.EnumerateDirectories(libDir))
            {
                ct.ThrowIfCancellationRequested();
                var rec = Path.GetFileName(recDir);
                if (!IsPlain(rec)) continue;
                foreach (var file in Directory.EnumerateFiles(recDir))
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(PartSuffix, StringComparison.Ordinal))
                    {
                        TryDelete(file);                                 // an interrupted write: never complete
                        continue;
                    }
                    if (ParseFileName(name) is not { } id) continue;     // not ours: left alone
                    var key = new ChunkKey(lib, rec, id.Stream, id.Chunk);
                    var size = new FileInfo(file).Length;
                    _entries[key] = new Entry(size, seqs.GetValueOrDefault(key));
                    _total += size;
                }
            }
        }
        _clock = Math.Max(_clock, _entries.Count == 0 ? 0 : _entries.Values.Max(e => e.Seq));
        TryDelete(Path.Combine(_dir, IndexFileName + ".tmp"));
    }

    private static (int Stream, int Chunk)? ParseFileName(string name)
    {
        var init = InitRegex().Match(name);
        if (init.Success) return (init.Groups[1].Value[0] - '0', 0);
        var c = FolderNames.ParseChunk(name);
        return c is { Temporary: false } ? (c.Stream, c.Number) : null;
    }

    private async Task<IndexDto?> LoadIndexAsync(CancellationToken ct)
    {
        var path = Path.Combine(_dir, IndexFileName);
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<IndexDto>(fs, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return null;                                                 // corrupt: disk scan alone rebuilds the cache
        }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IndexDto dto;
            lock (_gate)                                                 // snapshot under the index lock: the last writer wins with the newest state
            {
                dto = new IndexDto
                {
                    Clock = _clock,
                    Entries = [.. _entries.Select(kv => new EntryDto
                    {
                        Library = kv.Key.LibraryId, Recording = kv.Key.Recording, Stream = kv.Key.Stream,
                        Chunk = kv.Key.Chunk, Seq = kv.Value.Seq,
                    })],
                    Pinned = [.. _pinned.Select(p => new PinDto { Library = p.Library, Recording = p.Recording })],
                };
            }
            var final = Path.Combine(_dir, IndexFileName);
            var tmp = final + ".tmp";
            await File.WriteAllBytesAsync(tmp, JsonSerializer.SerializeToUtf8Bytes(dto), ct).ConfigureAwait(false);
            File.Move(tmp, final, overwrite: true);                      // atomic replace
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private string PathOf(ChunkKey key) =>
        Path.Combine(_dir, key.LibraryId, key.Recording,
            key.Chunk == 0 ? FolderNames.InitFile(key.Stream) : FolderNames.ChunkFile(key.Stream, key.Chunk));

    private static bool IsPlain(string name) => name is not "." and not ".." && PlainNameRegex().IsMatch(name);

    private static void ValidateName(string name)
    {
        if (!IsPlain(name)) throw new ArgumentException($"Not a plain folder name: '{name}'.", nameof(name));
    }

    private static void Validate(ChunkKey key)
    {
        ValidateName(key.LibraryId);
        ValidateName(key.Recording);
        ArgumentOutOfRangeException.ThrowIfNegative(key.Stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Stream, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(key.Chunk);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
