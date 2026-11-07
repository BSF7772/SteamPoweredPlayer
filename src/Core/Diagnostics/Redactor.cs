// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>
/// Keeps personal data out of reports: any free text that came from an exception, ffmpeg or mpv goes through
/// <see cref="Scrub"/> before it is written. Paths (drive and UNC), the user and machine names and every extra secret
/// (the library path, a game name) become placeholders.
/// </summary>
public static partial class Redactor
{
    [GeneratedRegex(@"[A-Za-z]:[\\/][^\s""'<>|*?]*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"\\\\[^\s""'<>|*?\\]+(?:\\[^\s""'<>|*?]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex UncPath();

    public static string Scrub(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        foreach (var secret in secrets.Append(Environment.UserName).Append(Environment.MachineName))
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 3) continue;
            s = s.Replace(secret, "<redacted>", StringComparison.OrdinalIgnoreCase);
            s = s.Replace(secret.Replace('\\', '/'), "<redacted>", StringComparison.OrdinalIgnoreCase);
        }
        s = DrivePath().Replace(s, "<path>");
        s = UncPath().Replace(s, "<path>");
        return s.Replace("\r", "").Replace('\n', ' ').Trim();
    }

    /// <summary>An exception as one safe line: its type and a scrubbed message.</summary>
    public static string Describe(Exception e, params string?[] secrets) => $"{e.GetType().Name}: {Scrub(e.Message, secrets)}";
}
