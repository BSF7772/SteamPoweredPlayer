// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Views.Player;

/// <summary>
/// The seek bar of the Player page: a filmstrip of trickplay thumbnails with the playhead, played part, gaps and A–B loop on top, the markers
/// lane below, and a thumbnail preview that follows the pointer. All times here are UI time (0 at the head of the recording); the page
/// converts. The control never talks to mpv: it raises <see cref="SeekRequested"/> and <see cref="MarkerInvoked"/>.
/// </summary>
public sealed partial class TimelineControl : UserControl
{
    private const double PreviewWidth = 232, PreviewGap = 12;

    private readonly TileCache _tiles = new();
    private readonly List<Rectangle> _gapRects = [];
    private IReadOnlyList<LaneMarker> _markers = [];
    private IReadOnlyList<(double Start, double End)> _gaps = [];
    private ISet<MarkerKind>? _visibleKinds;
    private TrickplayIndex? _trickplay;
    private double _mediaStart, _duration, _position, _filmDuration, _filmMediaStart;
    private double? _loopA, _loopB;
    private double _width;
    private int _filmVersion, _previewVersion;
    private long _lastScrubSeek;
    private bool _scrubbing;

    public TimelineControl() => InitializeComponent();

    /// <summary>The user clicked or dragged the strip: UI seconds. While dragging it is raised at most every 60 ms, and once more on release.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>The user clicked a marker icon in the lane.</summary>
    public event Action<LaneMarker>? MarkerInvoked;

    /// <summary>A drag began (true) or ended (false): the page ignores mpv's position meanwhile.</summary>
    public event Action<bool>? ScrubbingChanged;

    public bool IsScrubbing => _scrubbing;

    public double Duration => _duration;

    /// <summary>Sets everything that depends on the recording. Cheap to call again when a live session grew or was trimmed.</summary>
    public void Configure(double durationSeconds, double mediaStartSeconds, IReadOnlyList<(double Start, double End)> gaps,
        TrickplayIndex? trickplay, IReadOnlyList<LaneMarker> markers)
    {
        // A live session grows every few seconds: re-tile only when the layout drifted noticeably (a rebuild also restarts the tile loads).
        var filmChanged = !ReferenceEquals(trickplay, _trickplay) || Math.Abs(durationSeconds - _filmDuration) > Math.Max(6, _filmDuration * 0.1)
                          || Math.Abs(mediaStartSeconds - _filmMediaStart) > 6;
        _duration = Math.Max(0, durationSeconds);
        _mediaStart = mediaStartSeconds;
        _gaps = gaps;
        _trickplay = trickplay;
        _markers = markers;
        if (filmChanged) RebuildFilm();
        RebuildGaps();
        RebuildLane();
        UpdateVisuals();
    }

    /// <summary>Which marker kinds the lane shows (null: all).</summary>
    public void SetKindFilter(ISet<MarkerKind>? visible)
    {
        _visibleKinds = visible;
        RebuildLane();
    }

    /// <summary>The playhead, in UI seconds. Ignored while the user drags.</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (_scrubbing) return;
            _position = value;
            UpdateVisuals();
        }
    }

    /// <summary>The A–B loop in UI seconds (either may be null).</summary>
    public void SetLoop(double? a, double? b)
    {
        _loopA = a;
        _loopB = b;
        UpdateVisuals();
    }

    // ---- drawing --------------------------------------------------------------------------------------------------

    private double XOf(double seconds) => _duration <= 0 ? 0 : Math.Clamp(seconds / _duration, 0, 1) * _width;

    private double SecondsAt(double x) => _width <= 0 ? 0 : Math.Clamp(x / _width, 0, 1) * _duration;

    private void UpdateVisuals()
    {
        var x = XOf(_position);
        PlayedRect.Width = x;
        var left = Math.Clamp(x - 1.5, -1.5, Math.Max(0, _width - 1.5));
        Canvas.SetLeft(Playhead, left);
        Canvas.SetLeft(PlayheadHalo, left - 2);

        var loopVisible = _loopA is not null;
        LoopA.Visibility = loopVisible ? Visibility.Visible : Visibility.Collapsed;
        LoopB.Visibility = _loopB is not null ? Visibility.Visible : Visibility.Collapsed;
        LoopRect.Visibility = _loopA is not null && _loopB is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_loopA is { } a)
        {
            var ax = XOf(a);
            Canvas.SetLeft(LoopA, ax);
            if (_loopB is { } b)
            {
                var bx = XOf(b);
                Canvas.SetLeft(LoopB, bx - 3);
                Canvas.SetLeft(LoopRect, ax);
                LoopRect.Width = Math.Max(0, bx - ax);
            }
        }
    }

    private void Strip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var changed = Math.Abs(e.NewSize.Width - _width) > 0.5;
        _width = e.NewSize.Width;
        if (!changed) return;
        RebuildFilm();
        RebuildGaps();
        RebuildLane();
        UpdateVisuals();
    }

    private void RebuildGaps()
    {
        foreach (var r in _gapRects) Overlay.Children.Remove(r);
        _gapRects.Clear();
        if (_duration <= 0 || _width <= 0) return;
        foreach (var (start, end) in _gaps)
        {
            var x0 = XOf(start);
            var r = new Rectangle { Width = Math.Max(2, XOf(end) - x0), Height = 54, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB8, 0, 0, 0)) };
            Canvas.SetLeft(r, x0);
            Overlay.Children.Insert(0, r);
            _gapRects.Add(r);
        }
    }

    private void RebuildFilm()
    {
        var version = ++_filmVersion;
        _filmDuration = _duration;
        _filmMediaStart = _mediaStart;
        Film.Children.Clear();
        Film.ColumnDefinitions.Clear();
        if (_trickplay is not { Count: > 0 } trick || _width <= 0 || _duration <= 0) return;

        var aspect = trick.TileHeight > 0 ? (double)trick.TileWidth / trick.TileHeight : 16.0 / 9;
        var n = Math.Max(1, (int)Math.Ceiling(_width / (54 * aspect)));
        var tiles = new List<(int Col, TrickplayTile Tile)>(n);
        for (var i = 0; i < n; i++)
        {
            Film.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (trick.FindNearest(_mediaStart + (i + 0.5) / n * _duration) is { } tile) tiles.Add((i, tile));
        }
        _ = LoadFilmAsync(tiles, version);
    }

    private async Task LoadFilmAsync(List<(int Col, TrickplayTile Tile)> tiles, int version)
    {
        foreach (var (col, tile) in tiles)
        {
            var source = await _tiles.GetAsync(tile);
            if (version != _filmVersion) return;                      // a newer layout replaced this one
            if (source is null) continue;
            var image = new Image { Source = source, Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
            image.OpacityTransition = new ScalarTransition();
            Grid.SetColumn(image, col);
            Film.Children.Add(image);
            image.Opacity = 1;
        }
    }

    private void RebuildLane()
    {
        Lane.Children.Clear();
        if (_duration <= 0 || _width <= 0) return;
        foreach (var m in _markers)
        {
            if (_visibleKinds is not null && !_visibleKinds.Contains(m.Kind)) continue;
            if (!MarkerKinds.ShowOnLane(m.Kind)) continue;
            var x = XOf(m.Start);
            if (m.End is { } end)
            {
                var bar = new Rectangle
                {
                    Width = Math.Max(6, XOf(end) - x), Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = MarkerIcons.Brush(m.Kind, 200), IsHitTestVisible = false,
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, 22);
                Lane.Children.Add(bar);
            }
            var icon = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Tag = m,
                Background = MarkerIcons.Brush(m.Kind, 70), BorderBrush = MarkerIcons.Brush(m.Kind, 220), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = m.Glyph, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            ToolTipService.SetToolTip(icon, $"{m.Title}  ·  {MarkerIcons.Timecode(m.Start, false)}");
            icon.Tapped += Marker_Tapped;
            Canvas.SetLeft(icon, Math.Clamp(x - 11, 0, Math.Max(0, _width - 22)));
            Canvas.SetTop(icon, 0);
            Lane.Children.Add(icon);
        }
    }

    private void Marker_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: LaneMarker m }) MarkerInvoked?.Invoke(m);
    }

    // ---- pointer --------------------------------------------------------------------------------------------------

    private void Strip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Strip);
        if (!p.Properties.IsLeftButtonPressed || _duration <= 0) return;
        Strip.CapturePointer(e.Pointer);
        _scrubbing = true;
        ScrubbingChanged?.Invoke(true);
        Scrub(p.Position.X, force: true);
        e.Handled = true;
    }

    private void Strip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(Strip).Position.X;
        ShowHover(x);
        if (_scrubbing) Scrub(x, force: false);
    }

    private void Strip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        Scrub(e.GetCurrentPoint(Strip).Position.X, force: true);
        EndScrub(e.Pointer);
        e.Handled = true;
    }

    private void Strip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubbing) EndScrub(null);
    }

    private void Strip_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing) HideHover();
    }

    private void EndScrub(Pointer? pointer)
    {
        _scrubbing = false;
        if (pointer is not null) Strip.ReleasePointerCapture(pointer);
        ScrubbingChanged?.Invoke(false);
        UpdateVisuals();
    }

    private void Scrub(double x, bool force)
    {
        var t = SecondsAt(x);
        _position = t;
        UpdateVisuals();
        var now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastScrubSeek, now).TotalMilliseconds < 60) return;
        _lastScrubSeek = now;
        SeekRequested?.Invoke(t);
    }

    // ---- hover preview ----------------------------------------------------------------------------------------------

    private void ShowHover(double x)
    {
        if (_duration <= 0 || _width <= 0) return;
        x = Math.Clamp(x, 0, _width);
        var t = SecondsAt(x);
        Canvas.SetLeft(HoverLine, x);
        HoverLine.Visibility = Visibility.Visible;
        PreviewTime.Text = MarkerIcons.Timecode(t);
        Preview.HorizontalOffset = Math.Clamp(x - PreviewWidth / 2, 0, Math.Max(0, _width - PreviewWidth));
        Preview.VerticalOffset = -(126 + 8 + 24 + PreviewGap);
        Preview.IsOpen = true;

        var version = ++_previewVersion;
        var tile = _trickplay?.FindNearest(_mediaStart + t);
        if (tile is null) { PreviewImage.Source = null; return; }
        _ = ShowTileAsync(tile, version);
    }

    private async Task ShowTileAsync(TrickplayTile tile, int version)
    {
        var source = await _tiles.GetAsync(tile);
        if (version == _previewVersion && Preview.IsOpen) PreviewImage.Source = source;
    }

    private void HideHover()
    {
        HoverLine.Visibility = Visibility.Collapsed;
        Preview.IsOpen = false;
        _previewVersion++;
    }
}
