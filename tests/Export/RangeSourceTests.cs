// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Export;

public sealed class RangeSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spp-rs-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _bytes = new();

    public RangeSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private void Make(string name, int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        File.WriteAllBytes(P(name), b);
        _bytes[name] = b;
    }

    /// <summary>Init (50 B) plus chunks 1..5 on stream 0 with different sizes (100, 250, 37, 400, 120 B).</summary>
    private SessionMedia Fixture(params SegmentRun[] runs)
    {
        Make(FolderNames.InitFile(0), 50, 1);
        Make(FolderNames.InitFile(1), 20, 2);
        int[] sizes = [100, 250, 37, 400, 120];
        for (var i = 0; i < sizes.Length; i++)
        {
            Make(FolderNames.ChunkFile(0, i + 1), sizes[i], 10 + i);
            Make(FolderNames.ChunkFile(1, i + 1), 10 + i, 20 + i);
        }
        return new SessionMedia(_dir, runs.Length > 0 ? runs : [new SegmentRun(1, 5)], 3.0, false, true);
    }

    private byte[] Expected(int stream, params int[] chunks) =>
        [.. _bytes[FolderNames.InitFile(stream)], .. chunks.SelectMany(c => _bytes[FolderNames.ChunkFile(stream, c)])];

    private static async Task<int> ReadOnce(Stream s, CancellationToken ct = default) => await s.ReadAsync(new byte[10], ct);

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Bytes_equal_the_plain_concatenation_of_init_and_chunks()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 2, 4);
        Assert.Equal(Expected(0, 2, 3, 4), await ReadAllAsync(src));
    }

    [Fact]
    public async Task Audio_stream_uses_its_own_files()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 1, 1, 5);
        Assert.Equal(Expected(1, 1, 2, 3, 4, 5), await ReadAllAsync(src));
    }

    [Fact]
    public async Task Length_is_the_sum_of_sizes_captured_at_creation()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 3);
        Assert.Equal(50 + 100 + 250 + 37, src.Length);
        Assert.True(src.CanRead);
        Assert.True(src.CanSeek);
        Assert.False(src.CanWrite);
    }

    [Fact]
    public async Task Only_chunks_present_in_the_session_are_included()
    {
        var media = Fixture(new SegmentRun(1, 2), new SegmentRun(4, 5));   // chunk 3 is not part of the media
        await using var src = RangeSource.Create(media, 0, 1, 5);
        Assert.Equal(Expected(0, 1, 2, 4, 5), await ReadAllAsync(src));
    }

    [Fact]
    public async Task Range_with_no_chunk_in_the_session_is_just_the_init_segment()
    {
        var media = Fixture(new SegmentRun(1, 2));
        await using var src = RangeSource.Create(media, 0, 4, 5);
        Assert.Equal(Expected(0), await ReadAllAsync(src));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    [InlineData(50)]     // first byte of chunk 1
    [InlineData(149)]
    [InlineData(150)]    // first byte of chunk 2
    [InlineData(399)]
    [InlineData(400)]    // first byte of chunk 3
    [InlineData(436)]
    [InlineData(437)]    // first byte of chunk 4
    [InlineData(956)]    // last byte
    public async Task Seeking_anywhere_reads_the_same_bytes_as_the_concatenation(int offset)
    {
        var media = Fixture();
        var expected = Expected(0, 1, 2, 3, 4, 5);
        await using var src = RangeSource.Create(media, 0, 1, 5);
        Assert.Equal(expected.Length, src.Length);

        src.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[300];
        var got = 0;
        int n;
        while (got < buf.Length && (n = await src.ReadAsync(buf.AsMemory(got))) > 0) got += n;
        Assert.Equal(Math.Min(300, expected.Length - offset), got);
        Assert.Equal(expected.AsSpan(offset, got).ToArray(), buf.AsSpan(0, got).ToArray());
    }

    [Fact]
    public async Task Seek_origins_and_backwards_seeks_work()
    {
        var media = Fixture();
        var expected = Expected(0, 1, 2, 3, 4, 5);
        await using var src = RangeSource.Create(media, 0, 1, 5);
        var b = new byte[10];

        src.Seek(-10, SeekOrigin.End);
        await src.ReadExactlyAsync(b);
        Assert.Equal(expected[^10..], b);

        src.Seek(-25, SeekOrigin.Current);        // back across a chunk boundary
        Assert.Equal(expected.Length - 25, src.Position);
        await src.ReadExactlyAsync(b);
        Assert.Equal(expected.AsSpan(expected.Length - 25, 10).ToArray(), b);

        src.Position = 5;
        await src.ReadExactlyAsync(b);
        Assert.Equal(expected.AsSpan(5, 10).ToArray(), b);

        src.Seek(0, SeekOrigin.End);
        Assert.Equal(0, await src.ReadAsync(b));
        Assert.Throws<IOException>(() => src.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public void Sync_read_matches_the_concatenation()
    {
        var media = Fixture();
        using var src = RangeSource.Create(media, 0, 1, 5);
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        Assert.Equal(Expected(0, 1, 2, 3, 4, 5), ms.ToArray());
    }

    [Fact]
    public async Task Opening_works_while_another_reader_holds_the_files()
    {
        var media = Fixture();
        // A second reader (mpv, another export) and even Steam as a writer, all sharing everything.
        using var other = new FileStream(P(FolderNames.ChunkFile(0, 2)), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new FileStream(P(FolderNames.ChunkFile(0, 3)), FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        await using var a = RangeSource.Create(media, 0, 1, 5);
        await using var b = RangeSource.Create(media, 0, 1, 5);
        var expected = Expected(0, 1, 2, 3, 4, 5);
        Assert.Equal(expected, await ReadAllAsync(a));
        Assert.Equal(expected, await ReadAllAsync(b));
    }

    [Fact]
    public async Task Files_are_opened_lazily_one_at_a_time_and_closed_when_passed()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 5);
        // Nothing opened yet: exclusive access to every file works.
        AssertExclusive(FolderNames.InitFile(0), true);
        AssertExclusive(FolderNames.ChunkFile(0, 1), true);

        await src.ReadExactlyAsync(new byte[50 + 100 + 10]);    // init, chunk 1, 10 bytes into chunk 2

        AssertExclusive(FolderNames.InitFile(0), true);          // passed: closed
        AssertExclusive(FolderNames.ChunkFile(0, 1), true);      // passed: closed
        AssertExclusive(FolderNames.ChunkFile(0, 2), false);     // current: open
        AssertExclusive(FolderNames.ChunkFile(0, 3), true);      // not yet: still closed
    }

    private void AssertExclusive(string name, bool expectSuccess)
    {
        try
        {
            using var fs = new FileStream(P(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(expectSuccess, $"{name} was open although it should be closed");
        }
        catch (IOException)
        {
            Assert.False(expectSuccess, $"{name} was closed although it should be open");
        }
    }

    [Fact]
    public async Task Chunk_deleted_before_it_is_opened_throws_ChunkGone_with_its_number_after_the_earlier_bytes()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 5);
        File.Delete(P(FolderNames.ChunkFile(0, 3)));            // Steam trimmed it after we planned the export

        var head = new byte[50 + 100 + 250];                    // up to the end of chunk 2 works
        await src.ReadExactlyAsync(head);
        Assert.Equal(Expected(0, 1, 2), head);

        var ex = await Assert.ThrowsAsync<ChunkGoneException>(() => ReadOnce(src));
        Assert.Equal(3, ex.Chunk);
        Assert.IsAssignableFrom<IOException>(ex);
    }

    [Fact]
    public void Chunk_missing_at_creation_throws_ChunkGone()
    {
        var media = Fixture();
        File.Delete(P(FolderNames.ChunkFile(0, 2)));
        var ex = Assert.Throws<ChunkGoneException>(() => RangeSource.Create(media, 0, 1, 5));
        Assert.Equal(2, ex.Chunk);
    }

    [Fact]
    public void Missing_init_segment_throws_FileNotFound()
    {
        var media = Fixture();
        File.Delete(P(FolderNames.InitFile(0)));
        Assert.Throws<FileNotFoundException>(() => RangeSource.Create(media, 0, 1, 5));
    }

    [Fact]
    public async Task Chunk_that_shrinks_before_it_is_opened_throws_ChunkGone()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 5);
        using (var fs = new FileStream(P(FolderNames.ChunkFile(0, 4)), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.SetLength(10);

        await src.ReadExactlyAsync(new byte[50 + 100 + 250 + 37]);
        var ex = await Assert.ThrowsAsync<ChunkGoneException>(() => ReadOnce(src));
        Assert.Equal(4, ex.Chunk);
    }

    [Fact]
    public async Task Chunk_that_shrinks_while_it_is_being_read_throws_ChunkGone()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 5);
        await src.ReadExactlyAsync(new byte[50 + 100 + 10]);   // 10 bytes into chunk 2 (250 B), file is open
        using (var fs = new FileStream(P(FolderNames.ChunkFile(0, 2)), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.SetLength(20);
        await src.ReadExactlyAsync(new byte[10]);               // still inside the 20 bytes that remain
        var ex = await Assert.ThrowsAsync<ChunkGoneException>(() => ReadOnce(src));
        Assert.Equal(2, ex.Chunk);
    }

    /// <summary>
    /// Result on Windows 11 (NTFS), recorded as a test: <c>File.Delete</c> on a file that RangeSource holds open (it opens with
    /// FileShare.Delete) SUCCEEDS, so Steam's cleanup is never blocked. The already-open handle keeps serving the bytes of that
    /// chunk until it is passed, so the chunk being read stays complete. Chunks not opened yet are gone and report ChunkGone.
    /// </summary>
    [Fact]
    public async Task Deleting_the_open_chunk_succeeds_and_the_open_handle_keeps_reading()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 5);
        await src.ReadExactlyAsync(new byte[50 + 100 + 10]);    // chunk 2 is open

        File.Delete(P(FolderNames.ChunkFile(0, 2)));            // must not throw
        Assert.False(File.Exists(P(FolderNames.ChunkFile(0, 2))));

        var rest = new byte[250 - 10];                           // remainder of chunk 2 still readable
        await src.ReadExactlyAsync(rest);
        Assert.Equal(_bytes[FolderNames.ChunkFile(0, 2)][10..], rest);

        var next = new byte[37];                                 // and on into chunk 3, which still exists
        await src.ReadExactlyAsync(next);
        Assert.Equal(_bytes[FolderNames.ChunkFile(0, 3)], next);
    }

    [Fact]
    public async Task Deleting_the_open_chunk_then_seeking_back_into_it_reports_the_gap()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 5);
        await src.ReadExactlyAsync(new byte[50 + 100 + 10]);
        File.Delete(P(FolderNames.ChunkFile(0, 2)));
        src.Seek(60, SeekOrigin.Begin);                          // into chunk 1 (still there): closes chunk 2's handle
        await src.ReadExactlyAsync(new byte[10]);
        src.Seek(160, SeekOrigin.Begin);                         // chunk 2 again: gone for good
        var ex = await Assert.ThrowsAsync<ChunkGoneException>(() => ReadOnce(src));
        Assert.Equal(2, ex.Chunk);
    }

    [Fact]
    public async Task Read_honours_cancellation()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 5);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadOnce(src, cts.Token));
    }

    [Fact]
    public async Task Stream_is_read_only()
    {
        var media = Fixture();
        await using var src = RangeSource.Create(media, 0, 1, 5);
        Assert.Throws<NotSupportedException>(() => src.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => src.SetLength(1));
    }

    [Fact]
    public async Task Reading_after_dispose_throws()
    {
        var media = Fixture();
        var src = RangeSource.Create(media, 0, 1, 5);
        await src.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadOnce(src));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        var media = Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => RangeSource.Create(media, 2, 1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => RangeSource.Create(media, 0, 5, 1));
    }
}
