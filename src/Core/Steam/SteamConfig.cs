// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamPoweredPlayer.Core.Steam;

/// <summary>
/// The <c>GameRecording</c> block of <c>localconfig.vdf</c> (nothing else in that file is read). Missing block or keys give nulls.
/// Values in forms we have not verified are null and listed in <see cref="Diagnostics"/>, never guessed.
/// </summary>
/// <param name="PerGameEnabled">Per-app <c>PerGameSettings.&lt;appid&gt;.enabled</c>; absent apps follow Steam's default.</param>
public sealed partial record SteamConfig(
    int? BackgroundRecordMode,
    TimeSpan? BackgroundMaxKeep,
    string? BackgroundRecordPath,
    TimeSpan? InstantClipDuration,
    string? ExportDirectory,
    IReadOnlyDictionary<ulong, bool> PerGameEnabled,
    IReadOnlyList<string> Diagnostics)
{
    public static SteamConfig Empty { get; } = new(null, null, null, null, null, new Dictionary<ulong, bool>(), []);

    [GeneratedRegex(@"^(\d+)\s*(min|h|s)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    public static async Task<SteamConfig> LoadAsync(string localConfigPath, CancellationToken ct)
    {
        var doc = await Vdf.ReadTextFileAsync(localConfigPath, ct).ConfigureAwait(false);
        return doc is null ? Empty : Parse(doc);
    }

    public static SteamConfig Parse(string vdfText) => Parse(Vdf.ParseText(vdfText));

    public static SteamConfig Parse(VdfNode doc)
    {
        var block = doc.FindDescendant("GameRecording", 3);
        if (block is null || !block.IsBlock) return Empty;

        var diag = new List<string>();
        var mode = ParseInt(block, "BackgroundRecordMode", diag);
        var keep = ParseDuration(block, "BackgroundMaxKeep", bareSeconds: false, diag);
        var clip = ParseDuration(block, "InstantClipDuration", bareSeconds: true, diag);

        var perGame = new Dictionary<ulong, bool>();
        if (block.Get("PerGameSettings") is { } pgs)
        {
            foreach (var app in pgs.Children)
            {
                if (!ulong.TryParse(app.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
                var e = app.GetString("enabled");
                if (e == "0") perGame[id] = false;
                else if (e == "1") perGame[id] = true;
                else if (e is not null) diag.Add($"PerGameSettings.{app.Name}.enabled: unrecognised value '{e}'");
            }
        }

        return new SteamConfig(mode, keep, NonEmpty(block.GetString("BackgroundRecordPath")), clip,
            NonEmpty(block.GetString("ExportDirectory")), perGame, diag);
    }

    /// <summary>Known forms: <c>120min</c>, <c>2h</c>, <c>90s</c>. Anything else is unknown (null).</summary>
    public static TimeSpan? ParseDuration(string text, bool bareNumberIsSeconds = false)
    {
        text = text.Trim();
        if (bareNumberIsSeconds && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
            return TimeSpan.FromSeconds(bare);
        var m = DurationRegex().Match(text);
        if (!m.Success || !uint.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "min" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "s" => TimeSpan.FromSeconds(n),
            _ => null,
        };
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int? ParseInt(VdfNode block, string key, List<string> diag)
    {
        var s = block.GetString(key);
        if (s is null) return null;
        if (int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)) return v;
        diag.Add($"{key}: unrecognised value '{s}'");
        return null;
    }

    private static TimeSpan? ParseDuration(VdfNode block, string key, bool bareSeconds, List<string> diag)
    {
        var s = block.GetString(key);
        if (s is null) return null;
        var d = ParseDuration(s, bareSeconds);
        if (d is null) diag.Add($"{key}: unrecognised duration '{s}'");
        return d;
    }
}
