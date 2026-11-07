// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class ScanCacheTests
{
    private static FolderHeads Heads(params (string Name, long Len, long Ticks, HeadCheck H)[] items)
    {
        // Built through a scan-free path: parse a cache that contains them.
        var json = new StringBuilder("{\"version\":1,\"folders\":{\"video/bg_1_20250101_120000\":{\"chunks\":[");
        json.Append(string.Join(",", items.Select(i =>
        {
            var c = FolderNames.ParseChunk(i.Name)!;
            return $"[{c.Stream},{c.Number},{i.Len},{i.Ticks},{(i.H == HeadCheck.ZeroFilled ? 2 : 1)}]";
        })));
        json.Append("]}}}");
        return ScanCache.Parse(Encoding.UTF8.GetBytes(json.ToString())).Cache.Get("video/bg_1_20250101_120000")!;
    }

    [Fact]
    public void Round_trip_keeps_valid_and_zero_filled_heads()
    {
        var h = Heads(("chunk-stream0-00001.m4s", 40, 111, HeadCheck.Valid), ("chunk-stream1-00001.m4s", 41, 222, HeadCheck.ZeroFilled));
        var cache = ScanCache.From([new("video/bg_1_20250101_120000", h), new("clips/empty", FolderHeads.Empty)]);
        var parsed = ScanCache.Parse(cache.ToUtf8());
        Assert.Equal(ScanCacheStatus.Ok, parsed.Status);
        Assert.Single(parsed.Cache.Folders);                 // empty folder dropped
        var back = parsed.Cache.Get("video/bg_1_20250101_120000")!;
        Assert.True(back.SameAs(h));
        Assert.True(back.TryGet("chunk-stream1-00001.m4s", 41, 222, out var z) && z == HeadCheck.ZeroFilled);
        Assert.False(back.TryGet("chunk-stream1-00001.m4s", 42, 222, out _));      // length differs
        Assert.False(back.TryGet("chunk-stream1-00001.m4s", 41, 223, out _));      // mtime differs
    }

    [Fact]
    public void Corrupt_newer_and_missing_files_give_an_empty_cache_never_an_exception()
    {
        Assert.Equal(ScanCacheStatus.Corrupt, ScanCache.Parse("not json"u8).Status);
        Assert.Equal(ScanCacheStatus.Corrupt, ScanCache.Parse("{\"folders\":{}}"u8).Status);
        Assert.Equal(ScanCacheStatus.Corrupt, ScanCache.Parse("{\"version\":1,\"folders\":{\"a\":{\"chunks\":[[0,1]]}}}"u8).Status);
        var newer = ScanCache.Parse("{\"version\":2,\"folders\":{}}"u8);
        Assert.Equal(ScanCacheStatus.NewerVersion, newer.Status);
        Assert.Empty(newer.Cache.Folders);
    }

    [Fact]
    public async Task Store_writes_atomically_and_reads_back()
    {
        using var t = new TempDir();
        var folder = DataFolder.Open(t.Sub("data"), t.Sub("Rec"));
        var store = new ScanCacheStore(folder);
        Assert.Equal(ScanCacheStatus.Missing, (await store.ReadAsync()).Status);
        var cache = ScanCache.From([new("video/bg_1_20250101_120000", Heads(("chunk-stream0-00007.m4s", 40, 5, HeadCheck.Valid)))]);
        await store.WriteAsync(cache);
        Assert.False(File.Exists(folder.ScanCacheJsonTemp));
        var read = await store.ReadAsync();
        Assert.Equal(ScanCacheStatus.Ok, read.Status);
        Assert.True(read.Cache.Get("video/bg_1_20250101_120000")!.TryGet("chunk-stream0-00007.m4s", 40, 5, out _));
        await File.WriteAllTextAsync(folder.ScanCacheJson, "garbage");
        Assert.Equal(ScanCacheStatus.Corrupt, (await store.ReadAsync()).Status);
    }

    // ---- scanner with a trusted set ---------------------------------------------------------------------------------

    private sealed class Counter
    {
        public int Reads;
        public ScanOptions Options(TimeProvider clock) => new()
        {
            Clock = clock,
            HeadReader = (p, ct) => { Interlocked.Increment(ref Reads); return ScanHeads.ReadAsync(p, ct); },
        };
    }

    [Fact]
    public async Task Warm_scan_reads_no_heads_and_gives_the_same_result()
    {
        using var s = new SynthLibrary();
        var dir = s.AddBg("bg_730_20250101_120000", 1, 20);
        var c = new Counter();
        var cold = await Scanner.ScanCachedAsync(dir, null, c.Options(s.Clock));
        Assert.Equal(40, c.Reads);                                   // 20 chunks x (video + audio)
        Assert.Equal(40, cold.HeadReads);
        c.Reads = 0;
        var warm = await Scanner.ScanCachedAsync(dir, cold.Heads, c.Options(s.Clock));
        Assert.Equal(0, c.Reads);
        Assert.Equal(0, warm.HeadReads);
        Assert.Equal(cold.Result.Media!.Runs, warm.Result.Media!.Runs);
        Assert.Equal(cold.SizeBytes, warm.SizeBytes);
    }

    [Fact]
    public async Task New_chunk_is_read_changed_length_is_read_again()
    {
        using var s = new SynthLibrary();
        var dir = s.AddBg("bg_730_20250101_120000", 1, 5);
        var c = new Counter();
        var cold = await Scanner.ScanCachedAsync(dir, null, c.Options(s.Clock));

        c.Reads = 0;
        s.AddChunk(dir, 6);
        var grown = await Scanner.ScanCachedAsync(dir, cold.Heads, c.Options(s.Clock));
        Assert.Equal(2, c.Reads);                                    // only the two files of the new chunk
        Assert.Equal(6, grown.Result.Media!.LastChunk);

        c.Reads = 0;
        s.Write(dir, FolderNames.ChunkFile(0, 3), bytes: [.. SynthLibrary.BoxHead, .. new byte[99]], t: s.Old);   // other length, same mtime
        var changed = await Scanner.ScanCachedAsync(dir, grown.Heads, c.Options(s.Clock));
        Assert.Equal(1, c.Reads);
        Assert.Equal([new SegmentRun(1, 6)], changed.Result.Media!.Runs);
    }

    [Fact]
    public async Task Zero_filled_heads_are_remembered_too()
    {
        using var s = new SynthLibrary();
        var dir = s.AddBg("bg_730_20250101_120000", 1, 3);
        s.Write(dir, FolderNames.ChunkFile(0, 2), bytes: new byte[40]);
        var c = new Counter();
        var cold = await Scanner.ScanCachedAsync(dir, null, c.Options(s.Clock));
        Assert.Equal([new SegmentRun(1, 1), new SegmentRun(3, 3)], cold.Result.Media!.Runs);
        c.Reads = 0;
        var warm = await Scanner.ScanCachedAsync(dir, cold.Heads, c.Options(s.Clock));
        Assert.Equal(0, c.Reads);
        Assert.Equal([new SegmentRun(1, 1), new SegmentRun(3, 3)], warm.Result.Media!.Runs);
    }

    [Fact]
    public async Task Without_a_cache_the_scanner_behaves_as_before()
    {
        using var s = new SynthLibrary();
        var dir = s.AddBg("bg_730_20250101_120000", 1, 4);
        var plain = await Scanner.ScanAsync(dir, new() { Clock = s.Clock });
        var cached = await Scanner.ScanCachedAsync(dir, null, new() { Clock = s.Clock });
        Assert.Equal(plain.Status, cached.Result.Status);
        Assert.Equal(plain.Media!.Runs, cached.Result.Media!.Runs);
        Assert.Equal(ScanResult.Gone, (await Scanner.ScanCachedAsync(Path.Combine(s.Root, "video", "nope"), null)).Result);
    }
}
