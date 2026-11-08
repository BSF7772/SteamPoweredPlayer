// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.NetworkInformation;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Net;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Tests.Playback;

namespace SteamPoweredPlayer.Tests.Net;

public sealed class ChunkDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spp-dl-" + Guid.NewGuid().ToString("N")[..8]);
    private string Remote => Path.Combine(_root, "remote");
    private string CacheDir => Path.Combine(_root, "cache");
    private const int Chunks = 5;

    public ChunkDownloaderTests()
    {
        Directory.CreateDirectory(Remote);
        for (var stream = 0; stream < 2; stream++)
        {
            Write(FolderNames.InitFile(stream), 50, stream);
            for (var c = 1; c <= Chunks; c++) Write(FolderNames.ChunkFile(stream, c), 1000 + c, 10 * c + stream);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Write(string name, int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        File.WriteAllBytes(Path.Combine(Remote, name), b);
    }

    private SessionMedia Media() => new(Remote, [new SegmentRun(1, Chunks)], 3, false, false);
    private DownloadRequest Request() => new("net-x", "clip_1_20260101_000000", Media(), [.. Media().Chunks()]);
    private Task<LocalChunkCache> Cache() => LocalChunkCache.OpenAsync(CacheDir, 1L << 30);

    private void AssertIdentical()
    {
        var local = Path.Combine(CacheDir, "net-x", "clip_1_20260101_000000");
        foreach (var f in Directory.EnumerateFiles(Remote))
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(Path.Combine(local, Path.GetFileName(f))));
    }

    [Fact]
    public async Task Fetches_every_file_byte_exact_with_the_init_segments_first()
    {
        var files = new Flaky();
        var r = await new ChunkDownloader(await Cache(), files).RunAsync(Request(), null, default);
        Assert.Equal(DownloadState.Done, r.State);
        Assert.Equal(12, r.Done);
        Assert.All(files.Opened.Take(2), p => Assert.Contains("init-stream", p));
        AssertIdentical();
    }

    [Fact]
    public async Task A_connection_killed_mid_chunk_resumes_without_corruption()
    {
        var files = new Flaky { FailEveryOpen = 3, FailAfterBytes = 400 };
        var r = await new ChunkDownloader(await Cache(), files, parallel: 1).RunAsync(Request(), null, default);
        Assert.Equal(DownloadState.Done, r.State);
        Assert.Equal(0, r.Missing);
        Assert.True(files.Failures >= 3);
        AssertIdentical();
        Assert.Empty(Directory.EnumerateFiles(CacheDir, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_second_run_fetches_only_what_is_left()
    {
        var cache = await Cache();
        using var cts = new CancellationTokenSource();
        var files = new Flaky { OnOpened = n => { if (n == 5) cts.Cancel(); } };
        var first = await new ChunkDownloader(cache, files, parallel: 1).RunAsync(Request(), null, cts.Token);
        Assert.Equal(DownloadState.Cancelled, first.State);

        var second = new Flaky();
        var r = await new ChunkDownloader(cache, second, parallel: 1).RunAsync(Request(), null, default);
        Assert.Equal(DownloadState.Done, r.State);
        Assert.True(second.Opened.Count <= 12 - 4, $"re-fetched {second.Opened.Count} files");
        AssertIdentical();
    }

    [Fact]
    public async Task A_file_steam_removed_stays_a_gap()
    {
        File.Delete(Path.Combine(Remote, FolderNames.ChunkFile(1, 3)));
        var r = await new ChunkDownloader(await Cache(), new SharedFolderFiles()).RunAsync(Request(), null, default);
        Assert.Equal(DownloadState.Done, r.State);
        Assert.Equal(1, r.Missing);
    }

    [Fact]
    public async Task An_unreachable_server_times_out_and_a_later_run_resumes()
    {
        var cache = await Cache();
        var r = await new ChunkDownloader(cache, new Flaky { Down = true }, new JumpingTime()).RunAsync(Request(), null, default);
        Assert.Equal(DownloadState.CannotReach, r.State);
        Assert.Equal(0, r.Done);

        var again = await new ChunkDownloader(cache, new Flaky()).RunAsync(Request(), null, default);
        Assert.Equal(DownloadState.Done, again.State);
        AssertIdentical();
    }

    [Fact]
    public async Task A_seek_moves_the_fetch_order_to_the_playhead()
    {
        var files = new Flaky();
        var d = new ChunkDownloader(await Cache(), files, parallel: 1);
        d.Seek(4);
        await d.RunAsync(Request(), null, default);
        var chunks = files.Opened.Skip(2).Select(p => int.Parse(Path.GetFileNameWithoutExtension(p).Split('-')[^1])).Distinct().ToList();
        Assert.Equal([4, 5, 3, 2, 1], chunks);
    }

    [Fact]
    public async Task A_downloaded_clip_is_playable_from_the_cache_on_its_real_axis()
    {
        var clip = ClipAxisMpvTests.GenerateClip();
        try
        {
            var remote = (await Scanner.ScanAsync(clip)).Media!;
            var cache = await Cache();
            var recording = "clip_1_20260101_000000";
            var r = await new ChunkDownloader(cache, new SharedFolderFiles()).RunAsync(new DownloadRequest("net-x", recording, remote, [.. remote.Chunks()]), null, default);
            Assert.Equal(DownloadState.Done, r.State);
            var local = (await Scanner.ScanAsync(Path.Combine(CacheDir, "net-x", recording))).Media!;
            Assert.Equal(remote.ChunkCount, local.ChunkCount);
            Assert.Equal(ClipAxisMpvTests.Origin, local.AxisShiftSeconds);
        }
        finally { Directory.Delete(clip, true); }
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Wireless80211, LinkKind.Wireless)]
    [InlineData(NetworkInterfaceType.Ethernet, LinkKind.Wired)]
    [InlineData(NetworkInterfaceType.Tunnel, LinkKind.Unknown)]
    public void Link_kinds(NetworkInterfaceType type, LinkKind kind) => Assert.Equal(kind, LinkDetector.Classify(type));

    [Theory]
    [InlineData(@"\\nas\share\rec", "nas")]
    [InlineData(@"D:\rec", null)]
    public void Host_of_a_path(string path, string? host) => Assert.Equal(host, LinkDetector.HostOf(path));

    [Fact]
    public async Task A_local_path_is_a_local_link() => Assert.Equal(LinkKind.Local, await LinkDetector.DetectAsync(@"D:\rec", default));

    [Fact]
    public void Library_ids_are_stable_plain_and_ignore_case_and_trailing_slash()
    {
        var id = RemoteLibrary.IdFor(@"\\NAS\share\rec\");
        Assert.Equal(id, RemoteLibrary.IdFor(@"\\nas\share\rec"));
        Assert.Matches("^net-[0-9a-f]{12}$", id);
    }

    [Fact]
    public async Task Listing_finds_clips_and_cache_sessions_newest_first_and_skips_strangers()
    {
        var clip = ClipAxisMpvTests.GenerateClip();
        try
        {
            var root = Path.Combine(_root, "lib");
            foreach (var name in new[] { @"video\bg_730_20260102_000000", @"video\bg_730_20260101_000000", @"video\notes" })
            {
                var target = Path.Combine(root, name);
                Directory.CreateDirectory(target);
                foreach (var f in Directory.EnumerateFiles(clip)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
            }
            var list = await RemoteLibrary.ListAsync(root, default);
            Assert.Equal(["bg_730_20260102_000000", "bg_730_20260101_000000"], list.Select(r => r.Folder));
            await Assert.ThrowsAsync<IOException>(() => RemoteLibrary.ListAsync(Path.Combine(_root, "gone"), default));
        }
        finally { Directory.Delete(clip, true); }
    }

    private RemoteRecording Remote1() => new("clip_1_20260101_000000", FolderNames.ParseRecording("clip_1_20260101_000000")!, Media());

    private static async Task DrainAsync(DownloadQueue q)
    {
        while (q.Pending > 0) await Task.Delay(5);
        await q.IdleAsync();
    }

    [Fact]
    public async Task Queue_downloads_and_does_not_queue_a_recording_twice()
    {
        var q = new DownloadQueue(await Cache(), new Flaky());
        var a = q.Enqueue("net-x", Remote1());
        var b = q.Enqueue("net-x", Remote1());
        await DrainAsync(q);
        Assert.Equal(DownloadItemState.Done, a.State);
        Assert.Single(q.Items);
        Assert.True(a == b || b.State == DownloadItemState.Done);
        Assert.Equal(1, a.Fraction);
        AssertIdentical();
    }

    [Fact]
    public async Task Queue_resumes_on_a_network_change_after_giving_up()
    {
        var files = new Switchable();
        var q = new DownloadQueue(await Cache(), files, new JumpingTime());
        var item = q.Enqueue("net-x", Remote1());
        await DrainAsync(q);
        Assert.Equal(DownloadItemState.CannotReach, item.State);

        files.Down = false;
        q.NetworkChanged();
        await DrainAsync(q);
        Assert.Equal(DownloadItemState.Done, item.State);
        AssertIdentical();
    }

    [Fact]
    public async Task Queue_cancels()
    {
        var q = new DownloadQueue(await Cache(), new Switchable(), new JumpingTime());
        var item = q.Enqueue("net-x", Remote1());
        await DrainAsync(q);
        q.Cancel(item);                                 // from CannotReach
        Assert.Equal(DownloadItemState.Cancelled, item.State);
        q.NetworkChanged();
        await DrainAsync(q);
        Assert.Equal(DownloadItemState.Cancelled, item.State);
    }

    private sealed class Switchable : IRemoteFiles
    {
        public volatile bool Down = true;

        public ValueTask<long?> SizeAsync(string path, CancellationToken ct)
        {
            if (Down) throw new IOException("down");
            return new(new FileInfo(path).Length);
        }

        public Stream Open(string path) => new MemoryStream(File.ReadAllBytes(path));
    }

    /// <summary>Real files with injected failures.</summary>
    private sealed class Flaky : IRemoteFiles
    {
        private int _opens;
        public int FailEveryOpen { get; init; }
        public int FailAfterBytes { get; init; }
        public bool Down { get; init; }
        public Action<int>? OnOpened { get; init; }
        public int Failures { get; private set; }
        public List<string> Opened { get; } = [];

        public ValueTask<long?> SizeAsync(string path, CancellationToken ct)
        {
            if (Down) throw new IOException("down");
            return new(new FileInfo(path).Length);
        }

        public Stream Open(string path)
        {
            int n;
            lock (Opened) { Opened.Add(path); n = ++_opens; }
            OnOpened?.Invoke(n);
            Stream s = new MemoryStream(File.ReadAllBytes(path));
            if (FailEveryOpen > 0 && n % FailEveryOpen == 0)
            {
                Failures++;
                s = new DyingStream(s, FailAfterBytes);
            }
            return s;
        }
    }

    private sealed class DyingStream(Stream inner, int after) : Stream
    {
        private int _left = after;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) throw new IOException("connection reset");
            var n = inner.Read(buffer, offset, Math.Min(count, _left));
            _left -= n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left <= 0) throw new IOException("connection reset");
            var n = inner.Read(buffer.Span[..Math.Min(buffer.Length, _left)]);
            _left -= n;
            return new(n);
        }
    }

    /// <summary>Every look at the clock is 31 s later, so the 30 s reconnect window ends after one attempt.</summary>
    private sealed class JumpingTime : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Add(ref _now, 31_000);
    }
}
