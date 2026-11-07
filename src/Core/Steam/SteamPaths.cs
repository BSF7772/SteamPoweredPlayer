// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.Versioning;

namespace SteamPoweredPlayer.Core.Steam;

/// <summary>The Steam user that is signed in (or was last): <c>userdata/&lt;AccountId&gt;</c> uses the low 32 bits of the SteamID64.</summary>
public sealed record SteamUser(ulong SteamId64)
{
    public uint AccountId => unchecked((uint)(SteamId64 & 0xFFFFFFFFUL));
}

/// <summary>Locations inside a Steam install. Everything takes the Steam root as data, so tests use fixture folders.</summary>
public sealed record SteamPaths(string Root)
{
    public string LoginUsers => Path.Combine(Root, "config", "loginusers.vdf");
    public string LibraryFolders => Path.Combine(Root, "steamapps", "libraryfolders.vdf");
    public string AppInfo => Path.Combine(Root, "appcache", "appinfo.vdf");

    public string UserData(uint accountId) => Path.Combine(Root, "userdata", accountId.ToString(CultureInfo.InvariantCulture));
    public string LocalConfig(uint accountId) => Path.Combine(UserData(accountId), "config", "localconfig.vdf");
    public string Shortcuts(uint accountId) => Path.Combine(UserData(accountId), "config", "shortcuts.vdf");
    public string Grid(uint accountId) => Path.Combine(UserData(accountId), "config", "grid");
    public string LibraryCache(uint appId) => Path.Combine(Root, "appcache", "librarycache", appId.ToString(CultureInfo.InvariantCulture));

    /// <summary>The install folder from <c>HKCU\Software\Valve\Steam\SteamPath</c>; null when Steam is not installed (or not Windows).</summary>
    [SupportedOSPlatform("windows")]
    public static string? ReadSteamRootFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string p && p.Length > 0 ? Path.GetFullPath(p) : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Convenience wrapper: <see cref="ReadSteamRootFromRegistry"/> on Windows, null elsewhere.</summary>
    public static SteamPaths? FromRegistry() =>
        OperatingSystem.IsWindows() && ReadSteamRootFromRegistry() is { } root ? new SteamPaths(root) : null;

    /// <summary>The <c>loginusers.vdf</c> entry with <c>MostRecent</c> = 1; null when the file or such an entry is missing.</summary>
    public async Task<SteamUser?> ReadActiveUserAsync(CancellationToken ct)
    {
        var doc = await Vdf.ReadTextFileAsync(LoginUsers, ct).ConfigureAwait(false);
        return doc is null ? null : ParseActiveUser(doc);
    }

    public static SteamUser? ParseActiveUser(VdfNode doc)
    {
        var users = doc.Get("users") ?? doc;
        foreach (var u in users.Children)
        {
            if (!ulong.TryParse(u.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            if (u.GetString("MostRecent") == "1") return new SteamUser(id);
        }
        return null;
    }
}
