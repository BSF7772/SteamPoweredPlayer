// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace SteamPoweredPlayer.Core.Steam;

/// <summary>Steam's 64-bit game ids: a plain app id fits in 32 bits, a non-Steam shortcut is <c>(shortcutAppId &lt;&lt; 32) | 0x02000000</c>.</summary>
public static class GameIds
{
    public static bool IsShortcut(ulong gameId) => gameId > uint.MaxValue;

    public static uint ShortcutAppId(ulong gameId) => unchecked((uint)(gameId >> 32));

    public static ulong FromShortcut(uint shortcutAppId) => ((ulong)shortcutAppId << 32) | 0x02000000UL;
}

public enum GameNameSource { Shortcut, AppManifest, AppInfo, Index, Fallback }

public sealed record GameName(ulong GameId, string Name, GameNameSource Source);

/// <summary>Paths of artwork candidates found on disk (null = not found). Nothing is copied here.</summary>
public sealed record GameArtSources(string? Portrait, string? Hero, string? HeroBlur, string? Logo, string? Header)
{
    public static GameArtSources None { get; } = new(null, null, null, null, null);
}

/// <summary>
/// Game name resolution: shortcuts.vdf → appmanifest_&lt;appid&gt;.acf (every library) → appinfo.vdf → caller's index lookup → "App N".
/// Also finds artwork files. Reads are shared-delete, async and tolerate missing files.
/// </summary>
public sealed class GameNameResolver(SteamPaths paths, uint? accountId, Func<ulong, string?>? indexLookup = null)
{
    private static readonly string[] PortraitNames = ["library_600x900.jpg", "library_600x900_2x.jpg"];

    private Dictionary<uint, string>? _shortcuts;
    private IReadOnlyList<string>? _libraries;

    public static string Fallback(ulong gameId) =>
        "App " + (GameIds.IsShortcut(gameId) ? GameIds.ShortcutAppId(gameId) : gameId).ToString(CultureInfo.InvariantCulture);

    public async Task<GameName> ResolveAsync(ulong gameId, CancellationToken ct) =>
        (await ResolveManyAsync([gameId], ct).ConfigureAwait(false))[gameId];

    /// <summary>Resolves many ids with a single pass over appinfo.vdf (which can be 100+ MB).</summary>
    public async Task<IReadOnlyDictionary<ulong, GameName>> ResolveManyAsync(IEnumerable<ulong> gameIds, CancellationToken ct)
    {
        var result = new Dictionary<ulong, GameName>();
        var needInfo = new Dictionary<uint, List<ulong>>();

        foreach (var id in gameIds.Distinct())
        {
            if (GameIds.IsShortcut(id))
            {
                var map = await LoadShortcutsAsync(ct).ConfigureAwait(false);
                if (map.TryGetValue(GameIds.ShortcutAppId(id), out var n)) { result[id] = new(id, n, GameNameSource.Shortcut); continue; }
                Finish(result, id);
                continue;
            }
            var app = (uint)id;
            var manifest = await ReadManifestNameAsync(app, ct).ConfigureAwait(false);
            if (manifest is not null) { result[id] = new(id, manifest, GameNameSource.AppManifest); continue; }
            if (!needInfo.TryGetValue(app, out var l)) needInfo[app] = l = [];
            l.Add(id);
        }

        if (needInfo.Count > 0)
        {
            var names = await VdfAppInfo.ReadNamesAsync(paths.AppInfo, needInfo.Keys.ToHashSet(), ct).ConfigureAwait(false);
            foreach (var (app, ids) in needInfo)
            {
                foreach (var id in ids)
                {
                    if (names.TryGetValue(app, out var n)) result[id] = new(id, n, GameNameSource.AppInfo);
                    else Finish(result, id);
                }
            }
        }
        return result;
    }

    private void Finish(Dictionary<ulong, GameName> result, ulong id)
    {
        var idx = indexLookup?.Invoke(id);
        result[id] = !string.IsNullOrWhiteSpace(idx)
            ? new(id, idx, GameNameSource.Index)
            : new(id, Fallback(id), GameNameSource.Fallback);
    }

    // ---------------------------------------------------------------- sources

    private async Task<Dictionary<uint, string>> LoadShortcutsAsync(CancellationToken ct)
    {
        if (_shortcuts is not null) return _shortcuts;
        var map = new Dictionary<uint, string>();
        if (accountId is { } acc && await Vdf.ReadBinaryFileAsync(paths.Shortcuts(acc), ct).ConfigureAwait(false) is { } doc)
        {
            var list = doc.Get("shortcuts") ?? doc;
            foreach (var entry in list.Children)
            {
                var name = entry.GetString("AppName");
                if (entry.Get("appid") is { } a && a.TryGetUInt32(out var id) && !string.IsNullOrWhiteSpace(name)) map.TryAdd(id, name);
            }
        }
        return _shortcuts = map;
    }

    private async Task<IReadOnlyList<string>> LoadLibrariesAsync(CancellationToken ct)
    {
        if (_libraries is not null) return _libraries;
        var libs = new List<string> { paths.Root };
        if (await Vdf.ReadTextFileAsync(paths.LibraryFolders, ct).ConfigureAwait(false) is { } doc)
        {
            var folders = doc.Get("libraryfolders") ?? doc;
            foreach (var e in folders.Children)
            {
                if (!uint.TryParse(e.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                var p = e.IsBlock ? e.GetString("path") : e.Value; // old format: "1" "D:\\Lib"
                if (!string.IsNullOrWhiteSpace(p)) libs.Add(p);
            }
        }
        return _libraries = libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<string?> ReadManifestNameAsync(uint appId, CancellationToken ct)
    {
        var file = "appmanifest_" + appId.ToString(CultureInfo.InvariantCulture) + ".acf";
        foreach (var lib in await LoadLibrariesAsync(ct).ConfigureAwait(false))
        {
            var doc = await Vdf.ReadTextFileAsync(Path.Combine(lib, "steamapps", file), ct).ConfigureAwait(false);
            var name = doc?.GetString("AppState", "name");
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return null;
    }

    // -------------------------------------------------------------------- art

    /// <summary>Artwork candidates for a game: Steam's library cache for apps, the user's <c>config/grid</c> for shortcuts.</summary>
    public GameArtSources FindArt(ulong gameId)
    {
        if (GameIds.IsShortcut(gameId))
        {
            if (accountId is not { } acc) return GameArtSources.None;
            var grid = paths.Grid(acc);
            if (!Directory.Exists(grid)) return GameArtSources.None;
            var id = GameIds.ShortcutAppId(gameId).ToString(CultureInfo.InvariantCulture);
            return new(Grid(grid, id + "p"), Grid(grid, id + "_hero"), null, Grid(grid, id + "_logo"), Grid(grid, id));
        }
        if (gameId > uint.MaxValue) return GameArtSources.None;
        var dir = paths.LibraryCache((uint)gameId);
        if (!Directory.Exists(dir)) return GameArtSources.None;
        return new(Find(dir, PortraitNames), Find(dir, ["library_hero.jpg"]), Find(dir, ["library_hero_blur.jpg"]),
            Find(dir, ["logo.png"]), Find(dir, ["header.jpg"]));
    }

    private static string? Grid(string grid, string stem)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
        {
            var p = Path.Combine(grid, stem + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Top-level file first (older layout), then any subfolder (newer layouts keep art in hashed subfolders).</summary>
    private static string? Find(string dir, string[] names)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        try
        {
            foreach (var n in names)
            {
                var hit = Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
