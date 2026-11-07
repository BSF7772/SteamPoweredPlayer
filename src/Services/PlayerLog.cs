// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// Tiny append-only log of the player's own events (metrics, mpv warnings, failures), <c>%LocalAppData%\SteamPoweredPlayer\player.log</c>
/// (the sample's temp folder with <c>--sample</c>). It never contains recording paths or names. Truncated at 1 MB on start.
/// </summary>
public static class PlayerLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string? Path => _path;

    public static void Init(bool sample)
    {
        var dir = sample
            ? SampleSession.DefaultRoot
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPoweredPlayer");
        try
        {
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "player.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);      // our own log file
            lock (Gate) _path = path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* logging is best effort */ }
    }

    public static void Write(string message)
    {
        var path = _path;
        var line = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine;
        System.Diagnostics.Debug.Write(line);
        if (path is null) return;
        try { lock (Gate) File.AppendAllText(path, line); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
