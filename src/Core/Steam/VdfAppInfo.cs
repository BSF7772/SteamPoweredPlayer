// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace SteamPoweredPlayer.Core.Steam;

/// <summary>
/// Reads app names from <c>appcache/appinfo.vdf</c> (binary; magic 0x07564427/28/29). v29 keeps a string table at the end of the file
/// and its keys are indexes into it. Only the requested apps are parsed; the file is skipped through entry by entry.
/// Anything unexpected (unknown magic, truncation) returns what was found so far.
/// </summary>
public static class VdfAppInfo
{
    private const uint MagicBase = 0x07564400;
    private const int EntryFixedV27 = 4 + 4 + 8 + 20 + 4;    // state, updated, token, sha1, change number
    private const int EntryFixedV28 = EntryFixedV27 + 20;     // + binary-data sha1
    private const int MaxEntryBytes = 64 * 1024 * 1024;

    public static async Task<IReadOnlyDictionary<uint, string>> ReadNamesAsync(string path, IReadOnlySet<uint> appIds, CancellationToken ct)
    {
        var found = new Dictionary<uint, string>();
        if (appIds.Count == 0) return found;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 16, FileOptions.Asynchronous | FileOptions.RandomAccess);
            await ScanAsync(fs, appIds, found, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException) { }
        return found;
    }

    private static async Task ScanAsync(Stream fs, IReadOnlySet<uint> wanted, Dictionary<uint, string> found, CancellationToken ct)
    {
        var head = new byte[16];
        await fs.ReadExactlyAsync(head.AsMemory(0, 8), ct).ConfigureAwait(false);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if ((magic & 0xFFFFFF00) != MagicBase) return;
        var version = (int)(magic & 0xFF);
        if (version is < 0x27 or > 0x29) return;

        string[]? table = null;
        long entriesStart = 8;
        if (version >= 0x29)
        {
            await fs.ReadExactlyAsync(head.AsMemory(8, 8), ct).ConfigureAwait(false);
            var tableOffset = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8));
            entriesStart = 16;
            if (tableOffset <= entriesStart || tableOffset >= fs.Length) return;
            table = await ReadStringTableAsync(fs, tableOffset, ct).ConfigureAwait(false);
            if (table is null) return;
        }

        var fixedLen = version >= 0x28 ? EntryFixedV28 : EntryFixedV27;
        fs.Seek(entriesStart, SeekOrigin.Begin);
        var eh = new byte[8];
        while (fs.Position + 8 <= fs.Length && found.Count < wanted.Count) // the entry list ends with appid 0
        {
            ct.ThrowIfCancellationRequested();
            await fs.ReadExactlyAsync(eh, ct).ConfigureAwait(false);
            var appId = BinaryPrimitives.ReadUInt32LittleEndian(eh);
            if (appId == 0) return;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(eh.AsSpan(4));
            if (size < fixedLen || size > MaxEntryBytes || fs.Position + size > fs.Length) return;

            if (!wanted.Contains(appId)) { fs.Seek(size, SeekOrigin.Current); continue; }

            var buf = new byte[size];
            await fs.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
            var root = Vdf.ParseBinary(buf.AsMemory(fixedLen), table);
            var name = root.GetString("appinfo", "common", "name") ?? root.GetString("common", "name");
            if (!string.IsNullOrWhiteSpace(name)) found[appId] = name;
        }
    }

    private static async Task<string[]?> ReadStringTableAsync(Stream fs, long offset, CancellationToken ct)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        var rest = new byte[fs.Length - offset];
        await fs.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
        if (rest.Length < 4) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(rest);
        if (count > rest.Length) return null; // each string needs at least its terminator
        var table = new string[count];
        var pos = 4;
        for (var i = 0; i < count; i++)
        {
            var end = Array.IndexOf(rest, (byte)0, pos);
            if (end < 0) return null;
            table[i] = Encoding.UTF8.GetString(rest, pos, end - pos);
            pos = end + 1;
        }
        return table;
    }
}
