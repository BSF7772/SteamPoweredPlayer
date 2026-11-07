// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;
using Windows.UI;

namespace SteamPoweredPlayer.Views.Player;

/// <summary>Glyph, label and colour per <see cref="MarkerKind"/> (the "icon map" of plan §Phase 2), plus the small row models of the Player page.</summary>
public static class MarkerIcons
{
    public static string Glyph(MarkerKind kind) => kind switch
    {
        MarkerKind.Kill => "\U0001F52B",          // pistol
        MarkerKind.Death => "\U0001F480",         // skull
        MarkerKind.MultiKill => "\U0001F525",     // fire
        MarkerKind.Objective => "\U0001F4A3",     // bomb
        MarkerKind.Screenshot => "\U0001F4F7",    // camera
        MarkerKind.Achievement => "\U0001F3C6",   // trophy
        MarkerKind.Phase => "\U0001F3C1",         // chequered flag
        _ => "⭐",                            // star
    };

    public static string Label(MarkerKind kind) => kind switch
    {
        MarkerKind.Kill => "Kills",
        MarkerKind.Death => "Deaths",
        MarkerKind.MultiKill => "Multi-kills",
        MarkerKind.Objective => "Objectives",
        MarkerKind.Screenshot => "Screenshots",
        MarkerKind.Achievement => "Achievements",
        MarkerKind.Phase => "Phases",
        _ => "Other",
    };

    public static Color Color(MarkerKind kind) => kind switch
    {
        MarkerKind.Kill => Windows.UI.Color.FromArgb(255, 239, 68, 68),
        MarkerKind.Death => Windows.UI.Color.FromArgb(255, 156, 163, 175),
        MarkerKind.MultiKill => Windows.UI.Color.FromArgb(255, 245, 158, 11),
        MarkerKind.Objective => Windows.UI.Color.FromArgb(255, 37, 99, 235),
        MarkerKind.Screenshot => Windows.UI.Color.FromArgb(255, 6, 182, 212),
        MarkerKind.Achievement => Windows.UI.Color.FromArgb(255, 234, 179, 8),
        MarkerKind.Phase => Windows.UI.Color.FromArgb(255, 124, 58, 237),
        _ => Windows.UI.Color.FromArgb(255, 139, 92, 246),
    };

    public static SolidColorBrush Brush(MarkerKind kind, byte alpha = 255)
    {
        var c = Color(kind);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>Monospace timecode with one decimal: <c>m:ss.d</c> or <c>h:mm:ss.d</c> (0-based UI time).</summary>
    public static string Timecode(double seconds, bool tenths = true)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var whole = (long)Math.Floor(seconds);
        var t = TimeSpan.FromSeconds(whole);
        var d = (int)Math.Floor((seconds - whole) * 10);
        var tail = tenths ? string.Create(CultureInfo.InvariantCulture, $".{d}") : "";
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}{tail}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}{tail}");
    }
}

/// <summary>A marker on the UI (0-based) time axis, ready to draw. <see cref="Source"/> keeps the media-time original.</summary>
public sealed record LaneMarker(Marker Source, MarkerKind Kind, string Title, double Start, double? End)
{
    public bool IsRange => End is not null;
    public string Glyph => MarkerIcons.Glyph(Kind);
}

/// <summary>One row of the Events list.</summary>
public sealed class EventRow
{
    public required LaneMarker Marker { get; init; }

    public string Glyph => Marker.Glyph;
    public string Title => Marker.Title;
    public string Time => MarkerIcons.Timecode(Marker.Start, tenths: false);
    public string Detail => (Marker.IsRange ? $"{(Marker.End!.Value - Marker.Start).ToString("0", CultureInfo.InvariantCulture)} s · " : "") + MarkerIcons.Label(Marker.Kind).TrimEnd('s');
    public SolidColorBrush Accent => MarkerIcons.Brush(Marker.Kind, 60);
    public SolidColorBrush Stroke => MarkerIcons.Brush(Marker.Kind);
}

/// <summary>One row of the playlist (F5): a recording of the current game, with its thumbnail once loaded (F7).</summary>
public sealed class PlaylistRow : System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _thumb;

    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Meta { get; init; }
    public required string Glyph { get; init; }
    public bool Live { get; init; }

    /// <summary>Keeps what the thumbnail decodes from alive as long as the image can be shown (XAML may read it again).</summary>
    internal object? ThumbKeepAlive { get; set; }

    public ImageSource? Thumb
    {
        get => _thumb;
        set
        {
            if (ReferenceEquals(_thumb, value)) return;
            _thumb = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
