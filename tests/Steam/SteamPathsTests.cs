// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Steam;

public sealed class SteamPathsTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "steam");

    [Fact]
    public void Account_id_is_the_low_32_bits_of_the_steam_id()
    {
        // 76561197960265728 = base of individual accounts; + 12345
        Assert.Equal(12345u, new SteamUser(76561197960265728UL + 12345).AccountId);
    }

    [Fact]
    public void Paths_are_built_from_the_root()
    {
        var p = new SteamPaths(Path.Combine("x", "Steam"));
        Assert.Equal(Path.Combine("x", "Steam", "userdata", "42", "config", "localconfig.vdf"), p.LocalConfig(42));
        Assert.Equal(Path.Combine("x", "Steam", "userdata", "42", "config", "shortcuts.vdf"), p.Shortcuts(42));
        Assert.Equal(Path.Combine("x", "Steam", "userdata", "42", "config", "grid"), p.Grid(42));
        Assert.Equal(Path.Combine("x", "Steam", "steamapps", "libraryfolders.vdf"), p.LibraryFolders);
        Assert.Equal(Path.Combine("x", "Steam", "appcache", "appinfo.vdf"), p.AppInfo);
        Assert.Equal(Path.Combine("x", "Steam", "appcache", "librarycache", "730"), p.LibraryCache(730));
        Assert.Equal(Path.Combine("x", "Steam", "config", "loginusers.vdf"), p.LoginUsers);
    }

    [Fact]
    public async Task Active_user_is_the_most_recent_entry()
    {
        var user = await new SteamPaths(Fixtures).ReadActiveUserAsync(CancellationToken.None);
        Assert.Equal(new SteamUser(76561197960265728UL + 1001), user);
        Assert.Equal(1001u, user!.AccountId);
    }

    [Fact]
    public void Without_most_recent_the_only_user_is_active()
    {
        // Real Steam clients (2026) no longer write MostRecent = 1; the newest timestamp, or the only user, is the active one.
        var doc = Vdf.ParseText("\"users\" { \"76561197960266729\" { \"MostRecent\" \"0\" } }");
        Assert.Equal(new SteamUser(76561197960266729UL), SteamPaths.ParseActiveUser(doc));
    }

    [Fact]
    public void No_users_gives_null() => Assert.Null(SteamPaths.ParseActiveUser(Vdf.ParseText("\"users\" { }")));

    [Fact]
    public async Task Missing_login_users_gives_null()
    {
        Assert.Null(await new SteamPaths(Path.Combine(Path.GetTempPath(), "spp-nope-" + Guid.NewGuid())).ReadActiveUserAsync(CancellationToken.None));
    }
}
