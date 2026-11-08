// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Net;

namespace SteamPoweredPlayer.Tests.Net;

public sealed class LocalChunkCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spp-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Task<LocalChunkCache> Open(long cap = 10_000) => LocalChunkCache.OpenAsync(_dir, cap);

    private static ChunkKey K(int chunk, string rec = "bg_730_20250101_120000", string lib = "lib1", int stream = 0) =>
        new(lib, rec, stream, chunk);

    private static byte[] Bytes(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static Task<string> Put(LocalChunkCache c, ChunkKey k, int size, int seed = 1) =>
        c.PutAsync(k, new MemoryStream(Bytes(size, seed)), size);

    private IEnumerable<string> AllFiles(string pattern) =>
        Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, pattern, SearchOption.AllDirectories) : [];

    /// <summary>A stream that yields some bytes, then waits for a gate (or throws).</summary>
    private sealed class GatedStream(byte[] head, Task gate, bool thenThrow = false) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos < head.Length)
            {
                var n = Math.Min(buffer.Length, head.Length - _pos);
                head.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            await gate.WaitAsync(cancellationToken);
            if (thenThrow) throw new IOException("connection lost");
            return 0;
        }
    }

    [Fact]
    public async Task Put_then_lookup_returns_a_file_with_the_same_bytes()
    {
        var cache = await Open();
        var data = Bytes(120, 7);
        var path = await cache.PutAsync(K(5), new MemoryStream(data), data.Length);

        Assert.Equal(path, cache.TryGetPath(K(5)));
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.True(cache.Contains(K(5)));
        Assert.Equal(120, cache.TotalBytes);
        Assert.Equal(1, cache.Count);
        Assert.StartsWith(_dir, path);
        Assert.EndsWith("chunk-stream0-00005.m4s", path);
    }

    [Fact]
    public async Task Missing_key_is_not_cached()
    {
        var cache = await Open();
        Assert.Null(cache.TryGetPath(K(1)));
        Assert.False(cache.Contains(K(1)));
    }

    [Fact]
    public async Task Init_segment_key_uses_chunk_zero_and_its_own_file_name()
    {
        var cache = await Open();
        var path = await Put(cache, K(0, stream: 1), 30);
        Assert.EndsWith("init-stream1.m4s", path);
        Assert.Equal(path, cache.TryGetPath(K(0, stream: 1)));
        Assert.Null(cache.TryGetPath(K(0, stream: 0)));
    }

    [Fact]
    public async Task Keys_differ_by_library_recording_stream_and_chunk()
    {
        var cache = await Open();
        await Put(cache, K(1), 10);
        Assert.False(cache.Contains(K(1, lib: "lib2")));
        Assert.False(cache.Contains(K(1, rec: "bg_730_20250101_130000")));
        Assert.False(cache.Contains(K(1, stream: 1)));
        Assert.False(cache.Contains(K(2)));
    }

    [Fact]
    public async Task Write_goes_through_a_part_file_that_is_never_visible_as_complete()
    {
        var cache = await Open();
        var gate = new TaskCompletionSource();
        var data = Bytes(100, 3);
        var put = cache.PutAsync(K(1), new GatedStream(data, gate.Task), 100);

        await WaitFor(() => AllFiles("*.part").Any());                 // the write is in flight
        Assert.False(cache.Contains(K(1)));
        Assert.Null(cache.TryGetPath(K(1)));
        Assert.Empty(AllFiles("*.m4s"));

        gate.SetResult();
        var path = await put;
        Assert.Empty(AllFiles("*.part"));
        Assert.Equal(path, cache.TryGetPath(K(1)));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, cts.Token);
        }
    }

    [Fact]
    public async Task Interrupted_write_leaves_nothing_behind()
    {
        var cache = await Open();
        var gate = Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => cache.PutAsync(K(1), new GatedStream(Bytes(50, 1), gate, thenThrow: true), 100));
        Assert.False(cache.Contains(K(1)));
        Assert.Empty(AllFiles("*.part"));
        Assert.Empty(AllFiles("*.m4s"));
        Assert.Equal(0, cache.TotalBytes);
    }

    [Theory]
    [InlineData(99)]     // short
    [InlineData(101)]    // long
    public async Task Size_mismatch_is_rejected_and_cleaned(int actual)
    {
        var cache = await Open();
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.PutAsync(K(1), new MemoryStream(Bytes(actual, 1)), 100));
        Assert.False(cache.Contains(K(1)));
        Assert.Empty(AllFiles("*.part"));
        Assert.Empty(AllFiles("*.m4s"));
    }

    [Fact]
    public async Task Cancelled_write_leaves_nothing_behind()
    {
        var cache = await Open();
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var put = cache.PutAsync(K(1), new GatedStream(Bytes(10, 1), gate.Task), 100, cts.Token);
        await WaitFor(() => AllFiles("*.part").Any());
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => put);
        Assert.Empty(AllFiles("*.part"));
        Assert.False(cache.Contains(K(1)));
    }

    [Fact]
    public async Task Leftover_part_files_are_ignored_and_removed_when_the_cache_is_opened()
    {
        var recDir = Path.Combine(_dir, "lib1", "bg_730_20250101_120000");
        Directory.CreateDirectory(recDir);
        await File.WriteAllBytesAsync(Path.Combine(recDir, "chunk-stream0-00003.m4s.part"), Bytes(100, 1));

        var cache = await Open();
        Assert.False(cache.Contains(K(3)));
        Assert.Null(cache.TryGetPath(K(3)));
        Assert.Equal(0, cache.TotalBytes);
        Assert.Empty(AllFiles("*.part"));
    }

    [Fact]
    public async Task Contents_survive_reopening()
    {
        var cache = await Open();
        var p1 = await Put(cache, K(1), 100, 1);
        await Put(cache, K(2, stream: 1), 40, 2);

        var again = await Open();
        Assert.Equal(p1, again.TryGetPath(K(1)));
        Assert.True(again.Contains(K(2, stream: 1)));
        Assert.Equal(140, again.TotalBytes);
        Assert.Equal(2, again.Count);
    }

    [Fact]
    public async Task Files_removed_behind_the_cache_are_dropped_on_open_and_on_lookup()
    {
        var cache = await Open();
        var p1 = await Put(cache, K(1), 100);
        var p2 = await Put(cache, K(2), 100);

        File.Delete(p1);
        Assert.Null(cache.TryGetPath(K(1)));                     // vanished: lookup notices
        Assert.Equal(100, cache.TotalBytes);

        File.Delete(p2);
        var again = await Open();
        Assert.Equal(0, again.Count);
        Assert.Equal(0, again.TotalBytes);
    }

    [Fact]
    public async Task Bookkeeping_file_is_written_atomically_and_a_corrupt_one_is_survived()
    {
        var cache = await Open();
        await Put(cache, K(1), 100);
        await cache.PinAsync("lib1", "bg_730_20250101_120000");
        Assert.True(File.Exists(Path.Combine(_dir, "cache-index.json")));
        Assert.Empty(AllFiles("*.tmp"));

        await File.WriteAllTextAsync(Path.Combine(_dir, "cache-index.json"), "{ not json");
        var again = await Open();                                // files on disk stay the truth
        Assert.True(again.Contains(K(1)));
        Assert.Equal(100, again.TotalBytes);
        Assert.False(again.IsPinned("lib1", "bg_730_20250101_120000"));   // pins are lost with the file, nothing throws
    }

    [Fact]
    public async Task Least_recently_used_chunk_is_evicted_first()
    {
        var cache = await Open(cap: 300);
        await Put(cache, K(1), 100);
        await Put(cache, K(2), 100);
        await Put(cache, K(3), 100);
        Assert.NotNull(cache.TryGetPath(K(1)));                  // touch 1: now 2 is the oldest

        await Put(cache, K(4), 100);

        Assert.False(cache.Contains(K(2)));
        Assert.True(cache.Contains(K(1)));
        Assert.True(cache.Contains(K(3)));
        Assert.True(cache.Contains(K(4)));
        Assert.Equal(300, cache.TotalBytes);
        Assert.Equal(3, AllFiles("*.m4s").Count());              // really deleted from disk
    }

    [Fact]
    public async Task Eviction_order_survives_reopening()
    {
        var cache = await Open(cap: 300);
        await Put(cache, K(1), 100);
        await Put(cache, K(2), 100);
        await Put(cache, K(3), 100);
        cache.TryGetPath(K(1));
        await cache.FlushAsync();

        var again = await Open(cap: 300);
        await Put(again, K(4), 100);
        Assert.False(again.Contains(K(2)));
        Assert.True(again.Contains(K(1)));
    }

    [Fact]
    public async Task Evicts_several_chunks_when_one_big_one_arrives()
    {
        var cache = await Open(cap: 300);
        await Put(cache, K(1), 100);
        await Put(cache, K(2), 100);
        await Put(cache, K(3), 100);
        await Put(cache, K(4), 250);
        Assert.Equal([4], Present(cache, 1, 2, 3, 4));
        Assert.Equal(250, cache.TotalBytes);
    }

    private static int[] Present(LocalChunkCache c, params int[] chunks) => [.. chunks.Where(n => c.Contains(K(n)))];

    [Fact]
    public async Task Pinned_recordings_are_never_evicted()
    {
        var cache = await Open(cap: 300);
        await Put(cache, K(1, rec: "clip_730_20250101_120000"), 100);
        await Put(cache, K(2, rec: "clip_730_20250101_120000"), 100);
        await cache.PinAsync("lib1", "clip_730_20250101_120000");
        Assert.True(cache.IsPinned("lib1", "clip_730_20250101_120000"));

        await Put(cache, K(1, rec: "bg_730_20250101_120000"), 100);
        await Put(cache, K(2, rec: "bg_730_20250101_120000"), 100);   // over the cap: the unpinned recording pays

        Assert.True(cache.Contains(K(1, rec: "clip_730_20250101_120000")));
        Assert.True(cache.Contains(K(2, rec: "clip_730_20250101_120000")));
        Assert.False(cache.Contains(K(1, rec: "bg_730_20250101_120000")));
        Assert.True(cache.Contains(K(2, rec: "bg_730_20250101_120000")));

        await cache.UnpinAsync("lib1", "clip_730_20250101_120000");
        await Put(cache, K(3, rec: "bg_730_20250101_120000"), 100);   // now the old clip chunks are fair game
        Assert.False(cache.Contains(K(1, rec: "clip_730_20250101_120000")));
    }

    [Fact]
    public async Task Pins_survive_reopening_and_stay_per_library()
    {
        var cache = await Open();
        await cache.PinAsync("lib1", "clip_730_20250101_120000");
        var again = await Open();
        Assert.True(again.IsPinned("lib1", "clip_730_20250101_120000"));
        Assert.False(again.IsPinned("lib2", "clip_730_20250101_120000"));
    }

    [Fact]
    public async Task Cache_may_exceed_the_cap_when_everything_is_pinned()
    {
        var cache = await Open(cap: 150);
        await cache.PinAsync("lib1", "bg_730_20250101_120000");
        await Put(cache, K(1), 100);
        await Put(cache, K(2), 100);
        Assert.Equal(200, cache.TotalBytes);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task Chunks_in_use_by_playback_are_never_evicted_until_released()
    {
        var cache = await Open(cap: 200);
        await Put(cache, K(1), 100);
        await Put(cache, K(2), 100);
        var lease = cache.MarkInUse(K(1));                       // oldest, but playing

        await Put(cache, K(3), 100);
        Assert.True(cache.Contains(K(1)));
        Assert.False(cache.Contains(K(2)));

        lease.Dispose();
        lease.Dispose();                                         // idempotent
        await Put(cache, K(4), 100);
        Assert.False(cache.Contains(K(1)));
    }

    [Fact]
    public async Task Overlapping_leases_are_counted()
    {
        var cache = await Open(cap: 100);
        await Put(cache, K(1), 100);
        var a = cache.MarkInUse(K(1));
        var b = cache.MarkInUse(K(1));
        a.Dispose();
        await Put(cache, K(2), 100);
        Assert.True(cache.Contains(K(1)));                       // still leased by b
        b.Dispose();
        await Put(cache, K(3), 100);
        Assert.False(cache.Contains(K(1)));
    }

    [Fact]
    public async Task A_chunk_larger_than_the_cap_is_still_kept_while_nothing_else_can_go()
    {
        var cache = await Open(cap: 50);
        await Put(cache, K(1), 100);
        Assert.True(cache.Contains(K(1)));                       // the entry just written is never its own victim
    }

    [Fact]
    public async Task Opening_with_a_smaller_cap_trims_the_cache()
    {
        var cache = await Open(cap: 1000);
        await Put(cache, K(1), 100);
        await Put(cache, K(2), 100);
        var small = await Open(cap: 100);
        Assert.Equal(100, small.TotalBytes);
        Assert.False(small.Contains(K(1)));
        Assert.True(small.Contains(K(2)));
    }

    [Fact]
    public async Task Putting_the_same_key_again_replaces_the_file_and_the_size()
    {
        var cache = await Open();
        await Put(cache, K(1), 100, 1);
        var second = Bytes(60, 2);
        var path = await cache.PutAsync(K(1), new MemoryStream(second), 60);
        Assert.Equal(second, await File.ReadAllBytesAsync(path));
        Assert.Equal(60, cache.TotalBytes);
        Assert.Equal(1, cache.Count);
    }

    [Theory]
    [InlineData("..", "bg_1_20250101_000000")]
    [InlineData("lib1", "..")]
    [InlineData("lib1", "a/b")]
    [InlineData("lib1", "a\\b")]
    [InlineData("", "bg_1_20250101_000000")]
    [InlineData("lib1", "")]
    [InlineData("C:", "bg_1_20250101_000000")]
    public async Task Keys_that_could_escape_the_cache_folder_are_rejected(string lib, string rec)
    {
        var cache = await Open();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            cache.PutAsync(new ChunkKey(lib, rec, 0, 1), new MemoryStream(new byte[1]), 1));
        Assert.Throws<ArgumentException>(() => cache.TryGetPath(new ChunkKey(lib, rec, 0, 1)));
    }

    [Fact]
    public async Task Invalid_stream_chunk_or_size_are_rejected()
    {
        var cache = await Open();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.PutAsync(K(1, stream: 2), new MemoryStream(), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.PutAsync(K(-1), new MemoryStream(), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.PutAsync(K(1), new MemoryStream(), -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LocalChunkCache.OpenAsync(_dir, -1));
    }

    [Fact]
    public async Task Open_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalChunkCache.OpenAsync(_dir, 100, cts.Token));
    }

    [Fact]
    public async Task Concurrent_puts_of_different_chunks_all_land()
    {
        var cache = await Open(cap: 1_000_000);
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Put(cache, K(i), 1000 + i, i)));
        Assert.Equal(20, cache.Count);
        Assert.Equal(Enumerable.Range(1, 20).Sum(i => 1000L + i), cache.TotalBytes);
        Assert.Empty(AllFiles("*.part"));
    }
}
