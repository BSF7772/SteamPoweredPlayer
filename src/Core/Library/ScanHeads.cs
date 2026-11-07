// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>Chunk head check: an 8-byte read, classified. The I/O part opens with Read | Write | Delete sharing.</summary>
public static class ScanHeads
{
    public const int HeadLength = 8;

    /// <summary>Pure: is <paramref name="head"/> the start of an MP4 box? Size 0 (to end of file), 1 (64-bit size) or ≥ 8, printable type.</summary>
    public static HeadCheck Classify(ReadOnlySpan<byte> head)
    {
        if (head.Length < HeadLength) return HeadCheck.TooShort;
        head = head[..HeadLength];
        if (head.IndexOfAnyExcept((byte)0) < 0) return HeadCheck.ZeroFilled;
        var size = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
        if (size is not (0 or 1) && size < 8) return HeadCheck.NotABox;
        for (var i = 4; i < 8; i++)
        {
            var b = head[i];
            var ok = b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)' ';
            if (!ok) return HeadCheck.NotABox;
        }
        return HeadCheck.Valid;
    }

    /// <summary>Reads and classifies the head of <paramref name="path"/>. Never throws except for cancellation.</summary>
    public static async ValueTask<HeadCheck> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            var buf = new byte[HeadLength];
            var read = 0;
            while (read < HeadLength)
            {
                var n = await RandomAccess.ReadAsync(h, buf.AsMemory(read), read, ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            return Classify(buf.AsSpan(0, read));
        }
        catch (FileNotFoundException) { return HeadCheck.Vanished; }
        catch (DirectoryNotFoundException) { return HeadCheck.Vanished; }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return HeadCheck.Unreadable; }
        catch (UnauthorizedAccessException) { return HeadCheck.Unreadable; }
    }
}

/// <summary>Reads the start of <c>session.mpd</c>. It is never trusted, so every failure is an ordinary result.</summary>
public static class ScanManifest
{
    /// <summary>The <c>&lt;MPD</c> element, with its <c>type</c> attribute, is at the very start of the file.</summary>
    public const int ReadLength = 2048;

    public static ManifestKind Classify(ReadOnlySpan<byte> start)
    {
        if (start.IsEmpty) return ManifestKind.Empty;
        if (start.IndexOfAnyExcept((byte)0) < 0) return ManifestKind.ZeroFilled;
        var text = Encoding.UTF8.GetString(start);
        var at = text.IndexOf("<MPD", StringComparison.Ordinal);
        if (at < 0) return ManifestKind.Unrecognised;
        var end = text.IndexOf('>', at);
        var tag = end < 0 ? text.AsSpan(at) : text.AsSpan(at, end - at);
        return IsDynamic(tag) ? ManifestKind.Dynamic : ManifestKind.Static;
    }

    private static bool IsDynamic(ReadOnlySpan<char> tag)
    {
        // type = "dynamic" with optional whitespace and either quote; must not match e.g. mpdtype="dynamic".
        var from = 0;
        while (true)
        {
            var i = tag[from..].IndexOf("type", StringComparison.Ordinal);
            if (i < 0) return false;
            i += from;
            from = i + 4;
            if (i > 0 && !char.IsWhiteSpace(tag[i - 1])) continue;
            var j = from;
            while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
            if (j >= tag.Length || tag[j] != '=') continue;
            j++;
            while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
            if (j >= tag.Length || tag[j] is not ('"' or '\'')) continue;
            return tag[(j + 1)..].StartsWith("dynamic", StringComparison.Ordinal);
        }
    }

    public static async ValueTask<ManifestKind> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            var buf = new byte[ReadLength];
            var read = 0;
            while (read < buf.Length)
            {
                var n = await RandomAccess.ReadAsync(h, buf.AsMemory(read), read, ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            return Classify(buf.AsSpan(0, read));
        }
        catch (FileNotFoundException) { return ManifestKind.Missing; }
        catch (DirectoryNotFoundException) { return ManifestKind.Missing; }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return ManifestKind.Unreadable; }
        catch (UnauthorizedAccessException) { return ManifestKind.Unreadable; }
    }
}
