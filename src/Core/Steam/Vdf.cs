// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace SteamPoweredPlayer.Core.Steam;

/// <summary>One KeyValues node: a leaf (<see cref="Value"/>) or a block (<see cref="Children"/>). Lookups are case-insensitive.</summary>
public sealed class VdfNode
{
    public VdfNode(string name, string? value = null) { Name = name; Value = value; }

    public string Name { get; }
    public string? Value { get; set; }
    public List<VdfNode> Children { get; } = [];
    public bool IsBlock => Children.Count > 0;

    /// <summary>First direct child with that name (case-insensitive), or null.</summary>
    public VdfNode? Get(string name)
    {
        foreach (var c in Children)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>Follows a path of child names; null when any step is missing.</summary>
    public VdfNode? GetPath(params string[] path)
    {
        var n = this;
        foreach (var p in path)
        {
            n = n.Get(p);
            if (n is null) return null;
        }
        return n;
    }

    public string? GetString(params string[] path) => GetPath(path)?.Value;

    /// <summary>Depth-limited search for a descendant by name (first match in document order, direct children first).</summary>
    public VdfNode? FindDescendant(string name, int maxDepth)
    {
        if (maxDepth <= 0) return null;
        var direct = Get(name);
        if (direct is not null) return direct;
        foreach (var c in Children)
        {
            var r = c.FindDescendant(name, maxDepth - 1);
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>Interprets the value as a 32-bit id. Binary int32 values are stored signed, so negative numbers wrap to unsigned.</summary>
    public bool TryGetUInt32(out uint value)
    {
        value = 0;
        if (Value is null || !long.TryParse(Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return false;
        if (l < int.MinValue || l > uint.MaxValue) return false;
        value = unchecked((uint)l);
        return true;
    }
}

/// <summary>Tolerant KeyValues readers: Valve text (<c>.vdf</c>, <c>.acf</c>) and binary (<c>shortcuts.vdf</c>, <c>appinfo.vdf</c> payloads).</summary>
public static class Vdf
{
    private const int MaxBinaryDepth = 64;

    /// <summary>Reads a whole text file with delete-sharing; null when the file is missing or unreadable.</summary>
    public static async Task<string?> ReadAllTextAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sr = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return await sr.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Reads a whole binary file with delete-sharing; null when the file is missing or unreadable.</summary>
    public static async Task<byte[]?> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var ms = new MemoryStream();
            await fs.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static async Task<VdfNode?> ReadTextFileAsync(string path, CancellationToken ct)
    {
        var text = await ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return text is null ? null : ParseText(text);
    }

    public static async Task<VdfNode?> ReadBinaryFileAsync(string path, CancellationToken ct)
    {
        var bytes = await ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return bytes is null ? null : ParseBinary(bytes);
    }

    // ---------------------------------------------------------------- text

    /// <summary>Parses Valve text KeyValues into a nameless root. Malformed input yields whatever was read up to the problem.</summary>
    public static VdfNode ParseText(string text)
    {
        var root = new VdfNode("");
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        var pos = 0;

        while (NextToken(text, ref pos, out var tok, out var quoted))
        {
            if (!quoted && tok == "}") { if (stack.Count > 1) stack.Pop(); continue; }
            if (!quoted && tok == "{") continue; // stray brace

            if (!NextToken(text, ref pos, out var next, out var nextQuoted)) break;
            if (!nextQuoted && next == "}")
            {
                stack.Peek().Children.Add(new VdfNode(tok));
                if (stack.Count > 1) stack.Pop();
            }
            else if (!nextQuoted && next == "{")
            {
                var block = new VdfNode(tok);
                stack.Peek().Children.Add(block);
                stack.Push(block);
            }
            else
            {
                stack.Peek().Children.Add(new VdfNode(tok, next));
                SkipConditional(text, ref pos);
            }
        }
        return root;
    }

    /// <summary>A conditional suffix such as <c>[$WIN32]</c> after a value is ignored.</summary>
    private static void SkipConditional(string s, ref int pos)
    {
        var p = pos;
        SkipTrivia(s, ref p);
        if (p < s.Length && s[p] == '[')
        {
            var end = s.IndexOf(']', p);
            if (end >= 0) pos = end + 1;
        }
    }

    private static void SkipTrivia(string s, ref int p)
    {
        while (p < s.Length)
        {
            var c = s[p];
            if (char.IsWhiteSpace(c) || c == '﻿') p++;
            else if (c == '/' && p + 1 < s.Length && s[p + 1] == '/')
            {
                while (p < s.Length && s[p] != '\n') p++;
            }
            else break;
        }
    }

    private static bool NextToken(string s, ref int p, out string token, out bool quoted)
    {
        SkipTrivia(s, ref p);
        token = "";
        quoted = false;
        if (p >= s.Length) return false;

        if (s[p] == '"')
        {
            quoted = true;
            p++;
            var sb = new StringBuilder();
            while (p < s.Length && s[p] != '"')
            {
                var c = s[p++];
                if (c == '\\' && p < s.Length)
                {
                    switch (s[p])
                    {
                        case 'n': sb.Append('\n'); p++; break;
                        case 't': sb.Append('\t'); p++; break;
                        case '\\': sb.Append('\\'); p++; break;
                        case '"': sb.Append('"'); p++; break;
                        default: sb.Append('\\'); break; // unknown escape: keep the backslash, the next char is read normally
                    }
                }
                else sb.Append(c);
            }
            if (p < s.Length) p++; // closing quote
            token = sb.ToString();
            return true;
        }

        if (s[p] is '{' or '}') { token = s[p].ToString(); p++; return true; }

        var start = p;
        while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] is not ('"' or '{' or '}')) p++;
        token = s.Substring(start, p - start);
        return true;
    }

    // -------------------------------------------------------------- binary

    /// <summary>
    /// Parses binary KeyValues into a nameless root. With a <paramref name="stringTable"/> (appinfo v29) keys are int32 indexes into it.
    /// Truncated or unknown data yields the partial tree. Numbers are stored as invariant text (int32 signed, uint64 unsigned).
    /// </summary>
    public static VdfNode ParseBinary(ReadOnlyMemory<byte> data, IReadOnlyList<string>? stringTable = null)
    {
        var root = new VdfNode("");
        new BinaryKv(data, stringTable).ReadBlock(root, 0);
        return root;
    }

    private sealed class BinaryKv(ReadOnlyMemory<byte> data, IReadOnlyList<string>? table)
    {
        private int _pos;
        private bool _bad;

        public void ReadBlock(VdfNode parent, int depth)
        {
            if (depth > MaxBinaryDepth) { _bad = true; return; }
            while (!_bad && _pos < data.Length)
            {
                var type = data.Span[_pos++];
                if (type == 8) return;
                var key = ReadKey();
                if (_bad) return;
                switch (type)
                {
                    case 0:
                        var child = new VdfNode(key);
                        parent.Children.Add(child);
                        ReadBlock(child, depth + 1);
                        break;
                    case 1: parent.Children.Add(new VdfNode(key, ReadCString())); break;
                    case 2 or 4 or 6:
                        parent.Children.Add(new VdfNode(key, ReadInt32()?.ToString(CultureInfo.InvariantCulture)));
                        break;
                    case 3:
                        var f = ReadInt32();
                        parent.Children.Add(new VdfNode(key,
                            f is null ? null : BitConverter.Int32BitsToSingle(f.Value).ToString("R", CultureInfo.InvariantCulture)));
                        break;
                    case 5: parent.Children.Add(new VdfNode(key, ReadWString())); break;
                    case 7:
                        var u = ReadInt64();
                        parent.Children.Add(new VdfNode(key, u is null ? null : ((ulong)u.Value).ToString(CultureInfo.InvariantCulture)));
                        break;
                    case 10: parent.Children.Add(new VdfNode(key, ReadInt64()?.ToString(CultureInfo.InvariantCulture))); break;
                    default: _bad = true; return; // unknown type: stop, keep what we have
                }
            }
        }

        private string ReadKey()
        {
            if (table is null) return ReadCString();
            var idx = ReadInt32();
            if (idx is null) return "";
            if (idx < 0 || idx >= table.Count) { _bad = true; return ""; }
            return table[idx.Value];
        }

        private string ReadCString()
        {
            var span = data.Span[_pos..];
            var end = span.IndexOf((byte)0);
            if (end < 0) { _bad = true; _pos = data.Length; return Encoding.UTF8.GetString(span); }
            var s = Encoding.UTF8.GetString(span[..end]);
            _pos += end + 1;
            return s;
        }

        private string ReadWString()
        {
            var sb = new StringBuilder();
            while (_pos + 1 < data.Length)
            {
                var c = (char)(data.Span[_pos] | (data.Span[_pos + 1] << 8));
                _pos += 2;
                if (c == 0) return sb.ToString();
                sb.Append(c);
            }
            _bad = true;
            return sb.ToString();
        }

        private int? ReadInt32()
        {
            if (_pos + 4 > data.Length) { _bad = true; return null; }
            var v = BitConverter.ToInt32(data.Span.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        private long? ReadInt64()
        {
            if (_pos + 8 > data.Length) { _bad = true; return null; }
            var v = BitConverter.ToInt64(data.Span.Slice(_pos, 8));
            _pos += 8;
            return v;
        }
    }
}
