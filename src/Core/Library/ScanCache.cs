// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>A chunk file whose head was checked: its length and last write time (UTC ticks) when it was read, and the result.</summary>
public readonly record struct CachedChunk(long Length, long LastWriteTicks, HeadCheck Head);

/// <summary>
/// The real tfdt (seconds) of video chunk <see cref="Chunk"/>, the first chunk on disk when it was read (<see cref="ScanTfdt"/>), with
/// that file's length and last write time: trusted while they are unchanged and the chunk is still the first one.
/// </summary>
public readonly record struct CachedTfdt(int Chunk, long Length, long LastWriteTicks, double Seconds)
{
    public bool Matches(int chunk, long length, long lastWriteTicks) => Chunk == chunk && Length == length && LastWriteTicks == lastWriteTicks;
}

/// <summary>
/// The chunk heads already validated in one media folder (only <see cref="HeadCheck.Valid"/> and <see cref="HeadCheck.ZeroFilled"/> are
/// remembered; anything else is read again). A remembered result is trusted only while the file's name, length and last write time are
/// unchanged. Immutable.
/// </summary>
public sealed class FolderHeads
{
    public static FolderHeads Empty { get; } = new(new Dictionary<string, CachedChunk>(0, StringComparer.Ordinal));

    private readonly Dictionary<string, CachedChunk> _chunks;

    private FolderHeads(Dictionary<string, CachedChunk> chunks, CachedTfdt? tfdt = null)
    {
        _chunks = chunks;
        Tfdt = tfdt;
    }

    /// <summary>Wraps (and takes ownership of) <paramref name="chunks"/>; null or empty (and no tfdt) gives <see cref="Empty"/>.</summary>
    internal static FolderHeads From(Dictionary<string, CachedChunk>? chunks, CachedTfdt? tfdt = null) =>
        chunks is null or { Count: 0 } && tfdt is null ? Empty : new(chunks ?? new(0, StringComparer.Ordinal), tfdt);

    public int Count => _chunks.Count;

    /// <summary>The first chunk's real tfdt, when it was read (scan-cache entries written before it existed have none: read once).</summary>
    public CachedTfdt? Tfdt { get; }

    public IReadOnlyDictionary<string, CachedChunk> Chunks => _chunks;

    /// <summary>True (and the remembered result) when <paramref name="name"/> was checked with exactly this length and last write time.</summary>
    public bool TryGet(string name, long length, long lastWriteTicks, out HeadCheck head)
    {
        if (_chunks.TryGetValue(name, out var c) && c.Length == length && c.LastWriteTicks == lastWriteTicks)
        {
            head = c.Head;
            return true;
        }
        head = default;
        return false;
    }

    public bool SameAs(FolderHeads other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (_chunks.Count != other._chunks.Count || Tfdt != other.Tfdt) return false;
        foreach (var (k, v) in _chunks)
            if (!other._chunks.TryGetValue(k, out var o) || o != v) return false;
        return true;
    }
}

public enum ScanCacheStatus { Ok, Corrupt, NewerVersion, Missing }

public sealed record ScanCacheParse(ScanCacheStatus Status, ScanCache Cache, string? Error);

/// <summary>
/// <c>scan-cache.json</c> (desktop only, never read by phones): per recording folder (key = <c>RelativeFolder</c> as in
/// <c>library.json</c>) the chunk heads already validated, so a warm start does one directory listing per folder and head-reads only
/// new or changed files (plan §9: a cold scan of 21 000 chunks took 142 s). Format in <c>docs/data-folder.md</c>. Since Phase 2.1 a
/// folder may also carry <c>"tfdt": [chunk, length, lastWriteTicks, seconds]</c> (<see cref="CachedTfdt"/>); readers that predate it
/// ignore the key, and entries without it are read once. Immutable.
/// </summary>
public sealed class ScanCache
{
    public const int CurrentVersion = 1;

    public static ScanCache Empty { get; } = new(new Dictionary<string, FolderHeads>(0, StringComparer.Ordinal));

    private const byte CodeValid = 1, CodeZero = 2;

    private readonly Dictionary<string, FolderHeads> _folders;

    private ScanCache(Dictionary<string, FolderHeads> folders) => _folders = folders;

    public static ScanCache From(IEnumerable<KeyValuePair<string, FolderHeads>> folders)
    {
        var d = new Dictionary<string, FolderHeads>(StringComparer.Ordinal);
        foreach (var (k, v) in folders) if (v.Count > 0 || v.Tfdt is not null) d[k] = v;
        return d.Count == 0 ? Empty : new(d);
    }

    public IReadOnlyDictionary<string, FolderHeads> Folders => _folders;

    public FolderHeads? Get(string relativeFolder) => _folders.GetValueOrDefault(relativeFolder);

    /// <summary>Compact JSON (one folder per line is not needed: nobody edits it), UTF-8 without BOM.</summary>
    public byte[] ToUtf8()
    {
        using var ms = new MemoryStream(Math.Max(256, _folders.Count * 64 + _folders.Values.Sum(f => f.Count) * 44));
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("version", CurrentVersion);
            w.WriteStartObject("folders");
            foreach (var (folder, heads) in _folders.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(folder);
                w.WriteStartArray("chunks");
                foreach (var (name, c) in heads.Chunks.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    if (FolderNames.ParseChunk(name) is not { Temporary: false } n || FolderNames.ChunkFile(n.Stream, n.Number) != name) continue;
                    w.WriteStartArray();
                    w.WriteNumberValue(n.Stream);
                    w.WriteNumberValue(n.Number);
                    w.WriteNumberValue(c.Length);
                    w.WriteNumberValue(c.LastWriteTicks);
                    w.WriteNumberValue(c.Head == HeadCheck.ZeroFilled ? CodeZero : CodeValid);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                if (heads.Tfdt is { } t && double.IsFinite(t.Seconds))
                {
                    w.WriteStartArray("tfdt");
                    w.WriteNumberValue(t.Chunk);
                    w.WriteNumberValue(t.Length);
                    w.WriteNumberValue(t.LastWriteTicks);
                    w.WriteNumberValue(t.Seconds);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Never throws. Anything unreadable is <see cref="ScanCacheStatus.Corrupt"/> with an empty cache (the next scan is cold, nothing is lost).</summary>
    public static ScanCacheParse Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || !v.TryGetInt32(out var version) || version < 1)
                return new(ScanCacheStatus.Corrupt, Empty, "version missing");
            if (version > CurrentVersion) return new(ScanCacheStatus.NewerVersion, Empty, null);

            var folders = new Dictionary<string, FolderHeads>(StringComparer.Ordinal);
            if (root.TryGetProperty("folders", out var fs) && fs.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in fs.EnumerateObject())
                {
                    var chunks = new Dictionary<string, CachedChunk>();
                    if (f.Value.TryGetProperty("chunks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in arr.EnumerateArray())
                        {
                            var e = c.EnumerateArray();
                            e.MoveNext(); var stream = e.Current.GetInt32();
                            e.MoveNext(); var number = e.Current.GetInt32();
                            e.MoveNext(); var length = e.Current.GetInt64();
                            e.MoveNext(); var ticks = e.Current.GetInt64();
                            e.MoveNext(); var code = e.Current.GetInt32();
                            if (stream is not (0 or 1) || number < 0) continue;
                            var head = code switch { CodeValid => HeadCheck.Valid, CodeZero => HeadCheck.ZeroFilled, _ => (HeadCheck?)null };
                            if (head is { } h) chunks[FolderNames.ChunkFile(stream, number)] = new(length, ticks, h);
                        }
                    }
                    CachedTfdt? tfdt = null;
                    if (f.Value.TryGetProperty("tfdt", out var td) && td.ValueKind == JsonValueKind.Array && td.GetArrayLength() == 4)
                    {
                        var chunk = td[0].GetInt32();
                        var seconds = td[3].GetDouble();
                        if (chunk >= 0 && double.IsFinite(seconds) && seconds >= 0) tfdt = new(chunk, td[1].GetInt64(), td[2].GetInt64(), seconds);
                    }
                    if (chunks.Count > 0 || tfdt is not null) folders[f.Name] = FolderHeads.From(chunks, tfdt);
                }
            }
            return new(ScanCacheStatus.Ok, folders.Count == 0 ? Empty : new(folders), null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return new(ScanCacheStatus.Corrupt, Empty, e.Message);
        }
    }
}

/// <summary>Reads and writes <c>scan-cache.json</c> in the data folder; writes are atomic exactly like <see cref="IndexStore"/>.</summary>
public sealed class ScanCacheStore(DataFolder folder)
{
    public async Task WriteAsync(ScanCache cache, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = cache.ToUtf8();
        folder.EnsureCreated();
        var tmp = folder.ScanCacheJsonTemp;
        try
        {
            await using (var s = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await s.WriteAsync(bytes, ct).ConfigureAwait(false);
                await s.FlushAsync(ct).ConfigureAwait(false);
                s.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            IndexStore.Swap(tmp, folder.ScanCacheJson);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>A missing or unreadable file is an empty cache (<see cref="ScanCacheStatus.Missing"/> / <see cref="ScanCacheStatus.Corrupt"/>), never an exception.</summary>
    public async Task<ScanCacheParse> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            await using var fs = new FileStream(folder.ScanCacheJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var data = new byte[checked((int)fs.Length)];
            await fs.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            return ScanCache.Parse(data);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(ScanCacheStatus.Missing, ScanCache.Empty, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(ScanCacheStatus.Corrupt, ScanCache.Empty, e.Message); }
    }
}
