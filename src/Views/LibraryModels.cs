// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
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

    /// <summary>
    /// F8: decodes <paramref name="absolutePath"/> at the width it is shown (<paramref name="displayWidth"/> logical px × the DPI scale)
    /// and never above the file's own width (no upscaling in memory; XAML stretches what is left). Unknown size: the file's full size.
    /// </summary>
    public static ImageSource? Image(string? absolutePath, double displayWidth, double scale)
    {
        if (absolutePath is null) return null;
        try
        {
            var want = (int)Math.Ceiling(Math.Max(1, displayWidth) * Math.Max(1, scale));
            var natural = ImageHeader.Width(absolutePath);
            var image = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
            if (natural is { } n && want < n) image.DecodePixelWidth = want;
            image.UriSource = new Uri(absolutePath);
            return image;
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException) { return null; }
    }
}

/// <summary>Reads a JPEG's or PNG's pixel width from its header (a few KB, shared read), or null.</summary>
public static class ImageHeader
{
    public static int? Width(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            var buf = new byte[(int)Math.Min(fs.Length, 64 * 1024)];
            var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            var head = buf.AsSpan(0, n);
            if (n >= 24 && head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G')
                return head[16] << 24 | head[17] << 16 | head[18] << 8 | head[19];
            return Trickplay.TryGetJpegSize(head, out var w, out _) ? w : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}

/// <summary>
/// One row of the games rail. <see cref="GameId"/> is null for the "Downloaded clips" group (clips without a game). F11: a row is
/// created once per game and updated in place (<see cref="Update"/>), so live growth changes its numbers without re-animating the list.
/// </summary>
public sealed class GameTile : INotifyPropertyChanged
{
    public string? GameId { get; init; }
    public string Name { get; private set; } = "";
    public GameEntry? Entry { get; private set; }
    public ImageSource? Cover { get; private set; }
    public int Clips { get; private set; }
    public int Sessions { get; private set; }
    public long SizeBytes { get; private set; }
    public DateTimeOffset LastUtc { get; private set; }
    public string CoverPath { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public static GameTile Create(string? gameId, string name, GameEntry? entry, ImageSource? cover, string coverPath,
        int clips, int sessions, long sizeBytes, DateTimeOffset lastUtc) => new()
    {
        GameId = gameId, Name = name, Entry = entry, Cover = cover, CoverPath = coverPath,
        Clips = clips, Sessions = sessions, SizeBytes = sizeBytes, LastUtc = lastUtc,
    };

    /// <summary>Takes <paramref name="other"/>'s values (same <see cref="Key"/>) and raises one change notification when anything differs.</summary>
    public void Update(GameTile other)
    {
        if (other.Key != Key || other.Sig == Sig && Equals(other.Entry, Entry)) return;
        (Name, Entry, Cover, CoverPath, Clips, Sessions, SizeBytes, LastUtc) =
            (other.Name, other.Entry, other.Cover, other.CoverPath, other.Clips, other.Sessions, other.SizeBytes, other.LastUtc);
        PropertyChanged?.Invoke(this, new(string.Empty));     // all properties
    }

    /// <summary>Changes whenever anything the rail shows changed; the page replaces a tile only then (so selection and scroll survive a refresh).</summary>
    public string Sig => string.Create(CultureInfo.InvariantCulture, $"{Name}|{Clips}|{Sessions}|{SizeBytes}|{LastUtc.Ticks}|{CoverPath}");

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string Caption => Sessions > 0 ? $"{Fmt.Plural(Clips, "clip")} · {Fmt.Plural(Sessions, "session")}" : Fmt.Plural(Clips, "clip");
    public string Count => (Clips + Sessions).ToString(CultureInfo.InvariantCulture);
    public string Key => GameId ?? "(downloaded)";
    public bool NoCover => Cover is null;
}

/// <summary>One card of the clips grid. <see cref="Thumb"/> arrives later (Steam's <c>thumbnail.jpg</c>, else the first trickplay tile), hence the change notification.</summary>
public sealed class ClipTile : INotifyPropertyChanged
{
    private ImageSource? _thumb;

    public required RecordingEntry Entry { get; init; }

    public ImageSource? Thumb
    {
        get => _thumb;
        set
        {
            if (ReferenceEquals(_thumb, value)) return;
            _thumb = value;
            PropertyChanged?.Invoke(this, new(nameof(Thumb)));
            PropertyChanged?.Invoke(this, new(nameof(NoThumb)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The trickplay tile is being (or was) loaded; avoids queueing it again on every refresh.</summary>
    public bool ThumbRequested { get; set; }

    /// <summary>The thumbnail is Steam's own <c>thumbnail.jpg</c> (sharper than a trickplay tile): a new trickplay sheet does not replace it.</summary>
    public bool ThumbFromSteam { get; set; }

    /// <summary>The stream behind a decoded <c>thumbnail.jpg</c>: held as long as the image can be shown (a closed one fails fast in XAML).</summary>
    public object? ThumbKeepAlive { get; set; }

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
    public bool NoThumb => _thumb is null;
}
