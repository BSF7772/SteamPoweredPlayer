// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Steam;

public sealed class GameIdsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("spp-steam-").FullName;
    private readonly string _lib2 = Directory.CreateTempSubdirectory("spp-lib2-").FullName;
    private const uint Account = 1001;

    public void Dispose()
    {
        Directory.Delete(_root, true);
        Directory.Delete(_lib2, true);
    }

    private static readonly CancellationToken Ct = CancellationToken.None;

    private string Put(string root, string relative, byte[] bytes)
    {
        var p = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private string PutText(string root, string relative, string text) => Put(root, relative, Encoding.UTF8.GetBytes(text));

    private GameNameResolver Resolver(Func<ulong, string?>? index = null) => new(new SteamPaths(_root), Account, index);

    // ------------------------------------------------------------ id maths

    [Fact]
    public void FromShortcut_matches_the_known_value() => Assert.Equal(10307921510433554432UL, GameIds.FromShortcut(2400000000));

    [Fact]
    public void Shortcut_round_trip_and_classification()
    {
        var id = GameIds.FromShortcut(2400000000);
        Assert.True(GameIds.IsShortcut(id));
        Assert.Equal(2400000000u, GameIds.ShortcutAppId(id));
        Assert.False(GameIds.IsShortcut(730));
        Assert.False(GameIds.IsShortcut(uint.MaxValue));
    }

    // -------------------------------------------------------------- names

    [Fact]
    public async Task Shortcut_name_comes_from_shortcuts_vdf_with_signed_appid_and_case_variants()
    {
        var data = new BinKv().Begin("shortcuts")
            .Begin("0").Int("appid", unchecked((int)2400000000u)).Str("AppName", "Test Shortcut").End()
            .Begin("1").Int("appid", 12345).Str("appname", "Lower Case").End()
            .End().End().ToArray();
        Put(_root, @"userdata\1001\config\shortcuts.vdf", data);

        var r = Resolver();
        var a = await r.ResolveAsync(GameIds.FromShortcut(2400000000), Ct);
        Assert.Equal(new GameName(GameIds.FromShortcut(2400000000), "Test Shortcut", GameNameSource.Shortcut), a);
        Assert.Equal("Lower Case", (await r.ResolveAsync(GameIds.FromShortcut(12345), Ct)).Name);
    }

    [Fact]
    public async Task Unknown_shortcut_falls_back_to_index_then_app_n()
    {
        var id = GameIds.FromShortcut(2400000000);
        var viaIndex = await Resolver(g => g == id ? "From Index" : null).ResolveAsync(id, Ct);
        Assert.Equal(("From Index", GameNameSource.Index), (viaIndex.Name, viaIndex.Source));

        var none = await Resolver().ResolveAsync(id, Ct);
        Assert.Equal(("App 2400000000", GameNameSource.Fallback), (none.Name, none.Source));
    }

    [Fact]
    public async Task Manifest_is_searched_in_every_library_listed()
    {
        PutText(_root, @"steamapps\libraryfolders.vdf", "\"libraryfolders\"\n{\n\t\"0\" { \"path\" \"" + _root.Replace("\\", "\\\\") +
            "\" \"apps\" { \"111\" \"1\" } }\n\t\"1\" { \"path\" \"" + _lib2.Replace("\\", "\\\\") + "\" }\n}\n");
        PutText(_root, @"steamapps\appmanifest_111.acf", "\"AppState\" { \"appid\" \"111\" \"name\" \"Game In Main\" }");
        PutText(_lib2, @"steamapps\appmanifest_222.acf", "\"AppState\" { \"appid\" \"222\" \"name\" \"Game In Second\" }");

        var r = Resolver();
        Assert.Equal("Game In Main", (await r.ResolveAsync(111, Ct)).Name);
        var second = await r.ResolveAsync(222, Ct);
        Assert.Equal(("Game In Second", GameNameSource.AppManifest), (second.Name, second.Source));
    }

    [Fact]
    public async Task Old_libraryfolders_format_is_understood()
    {
        PutText(_root, @"steamapps\libraryfolders.vdf", "\"LibraryFolders\" { \"TimeNextStatsReport\" \"1\" \"1\" \"" + _lib2.Replace("\\", "\\\\") + "\" }");
        PutText(_lib2, @"steamapps\appmanifest_333.acf", "\"AppState\" { \"name\" \"Old Format Game\" }");
        Assert.Equal("Old Format Game", (await Resolver().ResolveAsync(333, Ct)).Name);
    }

    // ------------------------------------------------------------ appinfo

    private static byte[] AppInfo(int version, params (uint App, string Name)[] apps)
    {
        var table = new List<string>();
        var ms = new MemoryStream();
        void W32(uint v) => ms.Write(BitConverter.GetBytes(v));
        W32(0x07564400u | (uint)version);
        W32(1); // universe
        long tableOffsetPos = ms.Position;
        if (version >= 0x29) ms.Write(new byte[8]);
        var fixedLen = version >= 0x28 ? 60 : 40;
        foreach (var (app, name) in apps)
        {
            var kv = new BinKv(version >= 0x29 ? table : null).Begin("appinfo").Int("appid", (int)app)
                .Begin("common").Str("name", name).Str("type", "Game").End().End().End().ToArray();
            W32(app);
            W32((uint)(fixedLen + kv.Length));
            ms.Write(new byte[fixedLen]);
            ms.Write(kv);
        }
        W32(0);
        if (version >= 0x29)
        {
            var offset = ms.Position;
            W32((uint)table.Count);
            foreach (var t in table) { ms.Write(Encoding.UTF8.GetBytes(t)); ms.WriteByte(0); }
            var bytes = ms.ToArray();
            BitConverter.GetBytes(offset).CopyTo(bytes, tableOffsetPos);
            return bytes;
        }
        return ms.ToArray();
    }

    [Theory]
    [InlineData(0x27)]
    [InlineData(0x28)]
    [InlineData(0x29)]
    public async Task Appinfo_names_are_read_for_every_known_version(int version)
    {
        Put(_root, @"appcache\appinfo.vdf", AppInfo(version, (10, "Other Game"), (730, "Test Game"), (900, "Third Game")));
        var r = Resolver();
        var many = await r.ResolveManyAsync([730, 900, 5], Ct);
        Assert.Equal(("Test Game", GameNameSource.AppInfo), (many[730].Name, many[730].Source));
        Assert.Equal("Third Game", many[900].Name);
        Assert.Equal(("App 5", GameNameSource.Fallback), (many[5].Name, many[5].Source));
    }

    [Fact]
    public async Task Manifest_wins_over_appinfo()
    {
        Put(_root, @"appcache\appinfo.vdf", AppInfo(0x29, (730, "Info Name")));
        PutText(_root, @"steamapps\appmanifest_730.acf", "\"AppState\" { \"name\" \"Manifest Name\" }");
        Assert.Equal("Manifest Name", (await Resolver().ResolveAsync(730, Ct)).Name);
    }

    [Fact]
    public async Task Corrupt_or_truncated_appinfo_does_not_throw()
    {
        var good = AppInfo(0x29, (730, "Test Game"));
        Put(_root, @"appcache\appinfo.vdf", good[..30]);
        Assert.Equal("App 730", (await Resolver().ResolveAsync(730, Ct)).Name);

        Put(_root, @"appcache\appinfo.vdf", Encoding.ASCII.GetBytes("this is not an appinfo file at all"));
        Assert.Equal("App 730", (await Resolver().ResolveAsync(730, Ct)).Name);
    }

    [Fact]
    public async Task Index_lookup_is_used_before_the_app_n_fallback()
    {
        var g = await Resolver(id => id == 42 ? "Indexed" : null).ResolveAsync(42, Ct);
        Assert.Equal(("Indexed", GameNameSource.Index), (g.Name, g.Source));
    }

    // --------------------------------------------------------------- art

    [Fact]
    public void App_art_is_found_top_level_and_in_subfolders()
    {
        var cache = Path.Combine(_root, "appcache", "librarycache", "730");
        Put(cache, "library_600x900.jpg", [1]);
        Put(cache, "library_hero.jpg", [1]);
        Put(cache, @"abcdef0123\logo.png", [1]);
        Put(cache, @"abcdef0123\library_hero_blur.jpg", [1]);

        var art = Resolver().FindArt(730);
        Assert.Equal(Path.Combine(cache, "library_600x900.jpg"), art.Portrait);
        Assert.Equal(Path.Combine(cache, "library_hero.jpg"), art.Hero);
        Assert.Equal(Path.Combine(cache, "abcdef0123", "logo.png"), art.Logo);
        Assert.Equal(Path.Combine(cache, "abcdef0123", "library_hero_blur.jpg"), art.HeroBlur);
        Assert.Null(art.Header);
    }

    [Fact]
    public void Missing_art_folder_gives_none()
    {
        Assert.Equal(GameArtSources.None, Resolver().FindArt(999));
        Assert.Equal(GameArtSources.None, Resolver().FindArt(GameIds.FromShortcut(1)));
    }

    [Fact]
    public void Shortcut_art_comes_from_the_grid_folder()
    {
        var grid = Path.Combine(_root, "userdata", "1001", "config", "grid");
        Put(grid, "2400000000p.png", [1]);
        Put(grid, "2400000000_hero.jpg", [1]);
        Put(grid, "2400000000_logo.png", [1]);
        Put(grid, "2400000000.png", [1]);

        var art = Resolver().FindArt(GameIds.FromShortcut(2400000000));
        Assert.Equal(Path.Combine(grid, "2400000000p.png"), art.Portrait);
        Assert.Equal(Path.Combine(grid, "2400000000_hero.jpg"), art.Hero);
        Assert.Equal(Path.Combine(grid, "2400000000_logo.png"), art.Logo);
        Assert.Equal(Path.Combine(grid, "2400000000.png"), art.Header);
        Assert.Null(art.HeroBlur);
    }
}
