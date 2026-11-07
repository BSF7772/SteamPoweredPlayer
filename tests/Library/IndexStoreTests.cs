// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class IndexStoreTests
{
    private static (TempDir t, DataFolder f) Setup()
    {
        var t = new TempDir();
        return (t, DataFolder.Open(t.Sub("data"), t.Sub("Rec")));
    }

    private sealed class ThrowingStream(Stream inner, int throwAfter) : Stream
    {
        private int _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var allowed = Math.Max(0, throwAfter - _written);
            inner.Write(buffer[..Math.Min(allowed, buffer.Length)]);
            _written += buffer.Length;
            if (_written > throwAfter) throw new IOException("simulated disk failure");
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task Write_then_read_round_trips_and_leaves_no_temp_file()
    {
        var (t, f) = Setup();
        using var _ = t;
        var store = new IndexStore(f);
        Assert.Equal(LibraryIndexStatus.Missing, (await store.ReadAsync()).Status);

        await store.WriteAsync(LibraryIndexTests.Sample());
        var r = await store.ReadAsync();
        Assert.Equal(LibraryIndexStatus.Ok, r.Status);
        Assert.Equal(3, r.Index!.Recordings.Count);
        Assert.False(File.Exists(f.LibraryJsonTemp));
        Assert.Equal(LibraryIndexTests.Sample().ToUtf8(), File.ReadAllBytes(f.LibraryJson));
    }

    [Fact]
    public async Task Second_write_replaces_the_first()
    {
        var (t, f) = Setup();
        using var _ = t;
        var store = new IndexStore(f);
        await store.WriteAsync(LibraryIndexTests.Sample());
        var next = LibraryIndexTests.Sample() with { Recordings = [] };
        await store.WriteAsync(next);
        Assert.Empty((await store.ReadAsync()).Index!.Recordings);
        Assert.False(File.Exists(f.LibraryJsonTemp));
    }

    [Fact]
    public async Task Failure_mid_write_keeps_the_old_file_and_removes_the_temp()
    {
        var (t, f) = Setup();
        using var _ = t;
        await new IndexStore(f).WriteAsync(LibraryIndexTests.Sample());
        var before = File.ReadAllBytes(f.LibraryJson);

        var failing = new IndexStore(f, p => new ThrowingStream(new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None), 100));
        await Assert.ThrowsAsync<IOException>(() => failing.WriteAsync(LibraryIndexTests.Sample() with { Recordings = [] }));

        Assert.Equal(before, File.ReadAllBytes(f.LibraryJson));
        Assert.False(File.Exists(f.LibraryJsonTemp));
        Assert.Equal(LibraryIndexStatus.Ok, (await new IndexStore(f).ReadAsync()).Status);
    }

    [Fact]
    public async Task Failure_on_first_write_creates_no_library_json()
    {
        var (t, f) = Setup();
        using var _ = t;
        var failing = new IndexStore(f, p => new ThrowingStream(new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None), 10));
        await Assert.ThrowsAsync<IOException>(() => failing.WriteAsync(LibraryIndexTests.Sample()));
        Assert.False(File.Exists(f.LibraryJson));
        Assert.False(File.Exists(f.LibraryJsonTemp));
    }

    [Fact]
    public async Task Cancelled_token_leaves_the_old_file_intact()
    {
        var (t, f) = Setup();
        using var _ = t;
        var store = new IndexStore(f);
        await store.WriteAsync(LibraryIndexTests.Sample());
        var before = File.ReadAllBytes(f.LibraryJson);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(LibraryIndexTests.Sample() with { Recordings = [] }, cts.Token));
        Assert.Equal(before, File.ReadAllBytes(f.LibraryJson));
        Assert.False(File.Exists(f.LibraryJsonTemp));
    }

    [Fact]
    public async Task Cancellation_after_the_temp_is_written_but_before_the_swap_keeps_the_old_file()
    {
        var (t, f) = Setup();
        using var _ = t;
        await new IndexStore(f).WriteAsync(LibraryIndexTests.Sample());
        var before = File.ReadAllBytes(f.LibraryJson);

        using var cts = new CancellationTokenSource();
        var store = new IndexStore(f, p =>
        {
            cts.Cancel();    // fires once the temp file is opened; the write itself then observes the token
            return new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(LibraryIndexTests.Sample() with { Recordings = [] }, cts.Token));
        Assert.Equal(before, File.ReadAllBytes(f.LibraryJson));
        Assert.False(File.Exists(f.LibraryJsonTemp));
    }

    [Fact]
    public async Task Reader_holding_the_file_open_does_not_block_a_replace()
    {
        var (t, f) = Setup();
        using var _ = t;
        var store = new IndexStore(f);
        await store.WriteAsync(LibraryIndexTests.Sample());

        // A phone or a second process reading over SMB: opened like our own reader (read + write + delete sharing).
        using var reader = new FileStream(f.LibraryJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await store.WriteAsync(LibraryIndexTests.Sample() with { Recordings = [] });

        Assert.Empty((await store.ReadAsync()).Index!.Recordings);
    }

    [Fact]
    public async Task Concurrent_readers_during_writes_always_see_a_complete_file()
    {
        var (t, f) = Setup();
        using var _ = t;
        var store = new IndexStore(f);
        await store.WriteAsync(LibraryIndexTests.Sample());

        using var stop = new CancellationTokenSource();
        var bad = 0;
        var reads = 0;
        string? lastBad = null;
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var r = await store.ReadAsync();
                Interlocked.Increment(ref reads);
                if (r.Status != LibraryIndexStatus.Ok) { Interlocked.Increment(ref bad); lastBad = $"{r.Status} {r.Error}"; }
            }
        });
        for (var i = 0; i < 40; i++)
            await store.WriteAsync(i % 2 == 0 ? LibraryIndexTests.Sample() : LibraryIndexTests.Sample() with { Recordings = [] });
        await stop.CancelAsync();
        await reader;
        Assert.True(reads > 0);
        Assert.True(bad == 0, $"{bad} bad reads of {reads}; last: {lastBad}");
    }

    [Fact]
    public async Task Corrupt_file_on_disk_reads_as_corrupt()
    {
        var (t, f) = Setup();
        using var _ = t;
        f.EnsureCreated();
        await File.WriteAllBytesAsync(f.LibraryJson, new byte[64]);   // all zero bytes, like a stale Steam file
        Assert.Equal(LibraryIndexStatus.Corrupt, (await new IndexStore(f).ReadAsync()).Status);
    }
}
