// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Export;

/// <summary>
/// A chunk (or the init segment) vanished or shrank while a <see cref="RangeSource"/> was reading it: Steam trimmed its rolling
/// cache. <see cref="Chunk"/> is the chunk number (null for the init segment); callers report "Steam already removed X s"
/// from it (<see cref="SessionMedia.ChunkStartSeconds"/>).
/// </summary>
public sealed class ChunkGoneException : IOException
{
    public int? Chunk { get; }
    public string Path { get; }

    public ChunkGoneException(int? chunk, string path, Exception? inner = null)
        : base($"{(chunk is { } c ? $"Chunk {c}" : "The init segment")} is gone or was truncated: {System.IO.Path.GetFileName(path)}", inner)
    {
        Chunk = chunk;
        Path = path;
    }
}

/// <summary>
/// Read-only, seekable byte-concatenation of <c>init-streamS.m4s</c> and <c>chunk-streamS-a … chunk-streamS-b</c> (only chunks
/// present in the <see cref="SessionMedia"/>); a valid fragmented MP4 (docs/steam-recording-format.md).
/// Files are opened lazily, one at a time, with <c>FileShare.ReadWrite | FileShare.Delete</c> (never blocks Steam's cleanup) and
/// closed as soon as the stream moves past them. <see cref="Length"/> is the sum of the sizes captured at creation; a file that is
/// missing or shorter when it is reached raises <see cref="ChunkGoneException"/>.
/// <para>
/// Windows behaviour (verified in the tests): deleting a file while it is open succeeds; the open handle keeps serving that
/// chunk's bytes until the stream leaves it. Chunks not yet opened are gone.
/// </para>
/// </summary>
public sealed class RangeSource : Stream
{
    private readonly record struct Part(string Path, int? Chunk, long Length, long Start);

    private readonly Part[] _parts;
    private readonly long _length;
    private long _position;
    private int _openIndex = -1;
    private FileStream? _file;
    private bool _disposed;

    private RangeSource(Part[] parts, long length)
    {
        _parts = parts;
        _length = length;
    }

    /// <param name="stream">0 = video, 1 = audio.</param>
    /// <exception cref="FileNotFoundException">The init segment is missing.</exception>
    /// <exception cref="ChunkGoneException">A chunk of the range listed in <paramref name="media"/> is already gone.</exception>
    public static RangeSource Create(SessionMedia media, int stream, int firstChunk, int lastChunk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stream, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstChunk, lastChunk);
        return Build(media, stream, c => c >= firstChunk && c <= lastChunk);
    }

    /// <summary>The init segment and exactly the chunks in <paramref name="chunks"/>, in order: the chunks of several kept ranges, each once.</summary>
    public static RangeSource Create(SessionMedia media, int stream, IReadOnlySet<int> chunks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stream, 1);
        return Build(media, stream, chunks.Contains);
    }

    private static RangeSource Build(SessionMedia media, int stream, Func<int, bool> wanted)
    {
        var parts = new List<Part>();
        long start = 0;

        var init = System.IO.Path.Combine(media.Folder, FolderNames.InitFile(stream));
        var initInfo = new FileInfo(init);
        if (!initInfo.Exists) throw new FileNotFoundException("Init segment not found.", init);
        parts.Add(new Part(init, null, initInfo.Length, start));
        start += initInfo.Length;

        foreach (var chunk in media.Chunks().Where(wanted))
        {
            var path = System.IO.Path.Combine(media.Folder, FolderNames.ChunkFile(stream, chunk));
            var info = new FileInfo(path);
            if (!info.Exists) throw new ChunkGoneException(chunk, path);
            parts.Add(new Part(path, chunk, info.Length, start));
            start += info.Length;
        }
        return new RangeSource([.. parts], start);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0) throw new IOException("Cannot seek before the beginning of the stream.");
            _position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _length) return 0;
        var idx = FindPart(_position);
        var part = _parts[idx];
        var fs = Open(idx);
        var n = fs.Read(buffer[..(int)Math.Min(buffer.Length, part.Start + part.Length - _position)]);
        return Advance(idx, n);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || _position >= _length) return 0;
        var idx = FindPart(_position);
        var part = _parts[idx];
        var fs = Open(idx);
        var n = await fs.ReadAsync(buffer[..(int)Math.Min(buffer.Length, part.Start + part.Length - _position)], cancellationToken)
            .ConfigureAwait(false);
        return Advance(idx, n);
    }

    private int Advance(int idx, int n)
    {
        var part = _parts[idx];
        if (n == 0) throw Gone(part);                       // shrank under us: fewer bytes than captured
        _position += n;
        if (_position >= part.Start + part.Length) CloseCurrent();   // passed: release the handle at once
        return n;
    }

    private int FindPart(long position)
    {
        int lo = 0, hi = _parts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (_parts[mid].Start <= position) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private FileStream Open(int idx)
    {
        var part = _parts[idx];
        if (_openIndex != idx)
        {
            CloseCurrent();
            FileStream fs;
            try
            {
                fs = new FileStream(part.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 0, useAsync: true);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new ChunkGoneException(part.Chunk, part.Path, e);
            }
            if (fs.Length < part.Length)
            {
                fs.Dispose();
                throw Gone(part);
            }
            _file = fs;
            _openIndex = idx;
        }
        var local = _position - part.Start;
        if (_file!.Position != local) _file.Position = local;
        return _file;
    }

    private static ChunkGoneException Gone(Part part) => new(part.Chunk, part.Path);

    private void CloseCurrent()
    {
        _file?.Dispose();
        _file = null;
        _openIndex = -1;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing) CloseCurrent();
        }
        base.Dispose(disposing);
    }
}
