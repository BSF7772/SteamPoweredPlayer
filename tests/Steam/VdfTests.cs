// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Steam;

/// <summary>Tiny binary KeyValues writer for synthetic fixtures (shared by the other Steam tests).</summary>
internal sealed class BinKv
{
    private readonly MemoryStream _ms = new();
    private readonly List<string>? _table;

    public BinKv(List<string>? stringTable = null) => _table = stringTable;

    private void Key(string k)
    {
        if (_table is null) { Cstr(k); return; }
        var i = _table.IndexOf(k);
        if (i < 0) { _table.Add(k); i = _table.Count - 1; }
        _ms.Write(BitConverter.GetBytes(i));
    }

    private void Cstr(string s) { _ms.Write(Encoding.UTF8.GetBytes(s)); _ms.WriteByte(0); }

    public BinKv Begin(string k) { _ms.WriteByte(0); Key(k); return this; }
    public BinKv End() { _ms.WriteByte(8); return this; }
    public BinKv Str(string k, string v) { _ms.WriteByte(1); Key(k); Cstr(v); return this; }
    public BinKv Int(string k, int v) { _ms.WriteByte(2); Key(k); _ms.Write(BitConverter.GetBytes(v)); return this; }
    public BinKv U64(string k, ulong v) { _ms.WriteByte(7); Key(k); _ms.Write(BitConverter.GetBytes(v)); return this; }
    public byte[] ToArray() => _ms.ToArray();
}

public sealed class VdfTests
{
    [Fact]
    public void Text_parses_nested_blocks_case_insensitively()
    {
        var root = Vdf.ParseText("\"Outer\"\n{\n\t\"Inner\"\n\t{\n\t\t\"Key\"\t\t\"va\\\\lue\"\n\t}\n}\n");
        Assert.Equal("va\\lue", root.GetString("outer", "INNER", "key"));
    }

    [Fact]
    public void Text_ignores_comments_unquoted_tokens_and_conditionals()
    {
        var root = Vdf.ParseText("// head\nroot { a 1 [$WIN32]\n b \"two\" // tail\n c { d e } }");
        Assert.Equal("1", root.GetString("root", "a"));
        Assert.Equal("two", root.GetString("root", "b"));
        Assert.Equal("e", root.GetString("root", "c", "d"));
    }

    [Fact]
    public void Text_truncated_input_keeps_what_was_read()
    {
        var root = Vdf.ParseText("\"a\" { \"b\" \"1\" \"c\" { \"d\" \"2");
        Assert.Equal("1", root.GetString("a", "b"));
        Assert.Equal("2", root.GetString("a", "c", "d"));
        Assert.Null(Vdf.ParseText("").Get("x"));
    }

    [Fact]
    public void Text_unknown_escape_keeps_the_backslash()
    {
        var root = Vdf.ParseText("\"k\" \"D:\\Rec\\Test\"");
        Assert.Equal("D:\\Rec\\Test", root.GetString("k"));
    }

    [Fact]
    public void Binary_parses_strings_ints_and_nesting()
    {
        var data = new BinKv().Begin("shortcuts").Begin("0").Int("appid", -1894967296).Str("AppName", "Test Game").End().End().ToArray();
        var root = Vdf.ParseBinary(data);
        var e = root.GetPath("shortcuts", "0")!;
        Assert.Equal("Test Game", e.GetString("appname"));
        Assert.True(e.Get("appid")!.TryGetUInt32(out var id));
        Assert.Equal(2400000000u, id);
    }

    [Fact]
    public void Binary_with_string_table_resolves_keys()
    {
        var table = new List<string>();
        var data = new BinKv(table).Begin("appinfo").Begin("common").Str("name", "Test Game").U64("big", ulong.MaxValue).End().End().ToArray();
        var root = Vdf.ParseBinary(data, table);
        Assert.Equal("Test Game", root.GetString("appinfo", "common", "name"));
        Assert.Equal("18446744073709551615", root.GetString("appinfo", "common", "big"));
    }

    [Fact]
    public void Binary_truncated_returns_partial_tree_without_throwing()
    {
        var data = new BinKv().Begin("a").Str("x", "1").Str("y", "2").End().End().ToArray();
        var root = Vdf.ParseBinary(data.AsMemory(0, data.Length - 6));
        Assert.Equal("1", root.GetString("a", "x"));
    }

    [Fact]
    public void Binary_unknown_type_stops_parsing()
    {
        var root = Vdf.ParseBinary(new byte[] { 1, (byte)'a', 0, (byte)'1', 0, 99, (byte)'z', 0 });
        Assert.Equal("1", root.GetString("a"));
    }

    [Fact]
    public async Task Missing_file_gives_null()
    {
        Assert.Null(await Vdf.ReadTextFileAsync(Path.Combine(Path.GetTempPath(), "spp-nope-" + Guid.NewGuid()), CancellationToken.None));
        Assert.Null(await Vdf.ReadBinaryFileAsync(Path.Combine(Path.GetTempPath(), "spp-nope-" + Guid.NewGuid()), CancellationToken.None));
    }
}
