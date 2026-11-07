// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Views;

/// <summary>Display formatting shared by the pages. Numbers use the invariant culture (the UI strings are English).</summary>
public static class Fmt
{
    /// <summary>Monospace timecode: <c>m:ss</c> or <c>h:mm:ss</c>.</summary>
    public static string Timecode(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }

    public static string Span(TimeSpan t) => t.TotalHours >= 1
        ? (t.TotalHours % 1 == 0 ? $"{t.TotalHours:0} h" : $"{t.TotalHours:0.#} h").Replace(',', '.')
        : $"{t.TotalMinutes:0} min";

    public static string Ago(DateTimeOffset utc)
    {
        var d = DateTimeOffset.UtcNow - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} h ago";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays} d ago";
        return utc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    public static string DateTime(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var day = local.Date == today ? "Today" : local.Date == today.AddDays(-1) ? "Yesterday" : local.ToString("MMM d", CultureInfo.InvariantCulture);
        return day + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Bytes(long b) => MainWindow.FormatBytes(b);

    public static string Plural(int n, string one, string many = "") =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many.Length > 0 ? many : one + "s");

    /// <summary>The recording's length: the index value, else the complete chunks.</summary>
    public static double Duration(RecordingEntry r) =>
        r.DurationSeconds > 0 ? r.DurationSeconds : r.Runs.Sum(x => x.Count) * r.SegmentSeconds;

    public static ImageSource? Image(string? absolutePath)
    {
        if (absolutePath is null) return null;
        try { return new BitmapImage(new Uri(absolutePath)) { DecodePixelWidth = 640 }; }
        catch (Exception e) when (e is UriFormatException or ArgumentException) { return null; }
    }
}

/// <summary>One row of the games rail. <see cref="GameId"/> is null for the "Downloaded clips" group (clips without a game).</summary>
public sealed class GameTile
{
    public string? GameId { get; init; }
    public string Name { get; init; } = "";
    public GameEntry? Entry { get; init; }
    public ImageSource? Cover { get; init; }
    public int Clips { get; init; }
    public int Sessions { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset LastUtc { get; init; }

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string Caption => Sessions > 0 ? $"{Fmt.Plural(Clips, "clip")} · {Fmt.Plural(Sessions, "session")}" : Fmt.Plural(Clips, "clip");
    public string Count => (Clips + Sessions).ToString(CultureInfo.InvariantCulture);
    public string Key => GameId ?? "(downloaded)";
    public bool NoCover => Cover is null;
}

/// <summary>One card of the clips grid.</summary>
public sealed class ClipTile
{
    public required RecordingEntry Entry { get; init; }
    public ImageSource? Thumb { get; init; }

    public string Key => Entry.RelativeFolder;
    public string Title => Entry.Title ?? "Clip";
    public double Seconds => Fmt.Duration(Entry);
    public string Length => Fmt.Timecode(Seconds);
    public string When => Fmt.DateTime(Entry.StartUtc);
    public bool Downloaded => Entry.Kind == RecordingKind.DownloadedClip;
    public string KindGlyph => Downloaded ? "" : "";           // download / save
    public string KindText => Downloaded ? "Downloaded" : "Saved";
    public bool HasMarkers => Entry.MarkerCount > 0;
    public string Markers => "🏆 " + Entry.MarkerCount.ToString(CultureInfo.InvariantCulture);
    public string Meta => $"{When}  ·  {KindText}";
    public bool NoThumb => Thumb is null;
}
