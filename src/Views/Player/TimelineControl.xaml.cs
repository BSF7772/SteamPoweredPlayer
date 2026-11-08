// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using VirtualKey = Windows.System.VirtualKey;

namespace SteamPoweredPlayer.Views.Player;

/// <summary>
/// The seek bar of the Player page: a filmstrip of trickplay thumbnails with a single white pointer line, gaps and the A–B loop on top, the
/// markers lane below, a thumbnail preview that follows the pointer, and zoom (plan 2026-11-07 F1/F4: Ctrl+wheel around the cursor, − / +
/// around the pointer, the wheel or the scroll bar moves a zoomed window, double-click shows everything, the window follows the playhead).
/// All times here are UI time (0 at the head of the recording); the page converts. The control never talks to mpv: it raises
/// <see cref="SeekRequested"/> and <see cref="MarkerInvoked"/>.
/// </summary>
public sealed partial class TimelineControl : UserControl
{
    private const double PreviewWidth = 328, PreviewGap = 12, StripHeight = 54;

    /// <summary>After the user moved or zoomed the window, following the playhead waits this long (so the view isn't yanked back).</summary>
    private static readonly TimeSpan FollowPause = TimeSpan.FromSeconds(4);

    private readonly TileCache _tiles = new();
    private readonly TimelineView _view = new();
    private readonly List<Rectangle> _gapRects = [];
    private readonly RectangleGeometry _clip = new();
    private IReadOnlyList<LaneMarker> _markers = [];
    private IReadOnlyList<(double Start, double End)> _gaps = [];
    private ISet<MarkerKind>? _visibleKinds;
    private TrickplayIndex? _trickplay;
    private double _mediaStart, _position;
    private (double Duration, double MediaStart, double ViewStart, double ViewLength, double Width) _film;
    private double? _loopA, _loopB;
    private double _width;
    private int _filmVersion, _previewVersion;
    private long _lastScrubSeek, _userMovedAt;
    private bool _scrubbing, _syncingScroll;

    public TimelineControl()
    {
        InitializeComponent();
        Strip.Clip = _clip;
        Unloaded += (_, _) => HideHover();          // never leave the preview popup open over another page
    }

    /// <summary>The user clicked or dragged the strip: UI seconds. While dragging it is raised at most every 60 ms, and once more on release.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>The user clicked a marker icon in the lane.</summary>
    public event Action<LaneMarker>? MarkerInvoked;

    /// <summary>A drag began (true) or ended (false): the page ignores mpv's position meanwhile.</summary>
    public event Action<bool>? ScrubbingChanged;

    public bool IsScrubbing => _scrubbing;

    public double Duration => _view.Duration;

    /// <summary>The visible window (for tests and the uicheck).</summary>
    public TimelineView View => _view;

    /// <summary>The stream's frame rate, for the preview's timecode.</summary>
    public double Fps { get; set; } = FrameTimecode.FallbackFps;

    /// <summary>Sets everything that depends on the recording. Cheap to call again when a live session grew or was trimmed.</summary>
    public void Configure(double durationSeconds, double mediaStartSeconds, IReadOnlyList<(double Start, double End)> gaps,
        TrickplayIndex? trickplay, IReadOnlyList<LaneMarker> markers)
    {
        _view.SetDuration(durationSeconds);
        _mediaStart = mediaStartSeconds;
        _gaps = gaps;
        _trickplay = trickplay;
        _markers = markers;
        Redraw(force: false, trickplayChanged: !ReferenceEquals(trickplay, _filmTrick));
    }

    /// <summary>Shows the whole recording again (a new recording was opened).</summary>
    public void ResetZoom()
    {
        _view.Reset();
        Redraw(force: false, trickplayChanged: false);
    }

    /// <summary>Which marker kinds the lane shows (null: all).</summary>
    public void SetKindFilter(ISet<MarkerKind>? visible)
    {
        _visibleKinds = visible;
        RebuildLane();
    }

    /// <summary>The playhead, in UI seconds. Ignored while the user drags. A zoomed window follows it (F4).</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (_scrubbing) return;
            _position = value;
            if (Stopwatch.GetElapsedTime(_userMovedAt) > FollowPause && _view.Follow(value)) Redraw(force: false, trickplayChanged: false);
            else UpdateVisuals();
        }
    }

    /// <summary>The A–B loop in UI seconds (either may be null).</summary>
    public void SetLoop(double? a, double? b)
    {
        _loopA = a;
        _loopB = b;
        UpdateVisuals();
    }

    public void ZoomIn() => ZoomBy(TimelineView.StepFactor, _position);

    public void ZoomOut() => ZoomBy(1 / TimelineView.StepFactor, _position);

    // ---- drawing --------------------------------------------------------------------------------------------------

    private TrickplayIndex? _filmTrick;

    private double XOf(double seconds) => _view.XOf(seconds, _width);

    private double SecondsAt(double x) => _view.SecondsAt(x, _width);

    private void Redraw(bool force, bool trickplayChanged)
    {
        // A live session grows every few seconds: re-tile only when the layout drifted noticeably (a rebuild also restarts the tile loads).
        var f = _film;
        var drift = Math.Max(6, f.ViewLength * 0.1);
        var filmChanged = force || trickplayChanged || Math.Abs(_width - f.Width) > 0.5 || Math.Abs(_mediaStart - f.MediaStart) > 6
                          || Math.Abs(_view.Start - f.ViewStart) > (_view.IsZoomed ? _view.Length / 40 : drift)
                          || Math.Abs(_view.Length - f.ViewLength) > (_view.IsZoomed ? _view.Length / 40 : drift);
        if (filmChanged) RebuildFilm();
        RebuildGaps();
        RebuildLane();
        SyncZoomBar();
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var x = XOf(_position);
        var inView = _view.Duration > 0 && x >= -1 && x <= _width + 1;
        Playhead.Visibility = inView ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(Playhead, Math.Clamp(x - 1, 0, Math.Max(0, _width - 2)));

        LoopA.Visibility = _loopA is not null ? Visibility.Visible : Visibility.Collapsed;
        LoopB.Visibility = _loopB is not null ? Visibility.Visible : Visibility.Collapsed;
        LoopRect.Visibility = _loopA is not null && _loopB is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_loopA is { } a)
        {
            var ax = XOf(a);
            Canvas.SetLeft(LoopA, ax - 1);
            if (_loopB is { } b)
            {
                var bx = XOf(b);
                Canvas.SetLeft(LoopB, bx - 1);
                var left = Math.Max(0, ax);
                Canvas.SetLeft(LoopRect, left);
                LoopRect.Width = Math.Max(0, Math.Min(_width, bx) - left);
            }
        }
    }

    private void SyncZoomBar()
    {
        _syncingScroll = true;
        var zoomed = _view.IsZoomed;
        ViewScroll.Visibility = zoomed ? Visibility.Visible : Visibility.Collapsed;
        ViewScroll.Maximum = zoomed ? _view.Duration - _view.Length : 0;
        ViewScroll.ViewportSize = Math.Max(0.001, _view.Length);
        ViewScroll.LargeChange = Math.Max(0.001, _view.Length * 0.9);
        ViewScroll.SmallChange = Math.Max(0.001, _view.Length * 0.1);
        ViewScroll.Value = _view.Start;
        _syncingScroll = false;
        var z = _view.Zoom;
        ZoomText.Text = (z < 10 ? z.ToString("0.#", CultureInfo.InvariantCulture) : z.ToString("0", CultureInfo.InvariantCulture)) + "×";
        ZoomOutButton.IsEnabled = zoomed;
        ZoomInButton.IsEnabled = _view.Length > Math.Min(TimelineView.MinLengthSeconds, _view.Duration) + 1e-9;
    }

    private void Strip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _clip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        var changed = Math.Abs(e.NewSize.Width - _width) > 0.5;
        _width = e.NewSize.Width;
        if (changed) Redraw(force: true, trickplayChanged: false);
    }

    private void RebuildGaps()
    {
        foreach (var r in _gapRects) Overlay.Children.Remove(r);
        _gapRects.Clear();
        if (_view.Duration <= 0 || _width <= 0) return;
        foreach (var (start, end) in _gaps)
        {
            if (end < _view.Start || start > _view.End) continue;
            var x0 = Math.Max(0, XOf(start));
            var x1 = Math.Min(_width, XOf(end));
            var r = new Rectangle { Width = Math.Max(2, x1 - x0), Height = StripHeight, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB8, 0, 0, 0)) };
            Canvas.SetLeft(r, x0);
            Overlay.Children.Insert(0, r);
            _gapRects.Add(r);
        }
    }

    /// <summary>Tiles the visible window (zoomed: the tiles of that range, so the strip shows more detail).</summary>
    private void RebuildFilm()
    {
        var version = ++_filmVersion;
        _film = (_view.Duration, _mediaStart, _view.Start, _view.Length, _width);
        _filmTrick = _trickplay;
        Film.Children.Clear();
        Film.ColumnDefinitions.Clear();
        if (_trickplay is not { Count: > 0 } trick || _width <= 0 || _view.Length <= 0) return;

        var aspect = trick.TileHeight > 0 ? (double)trick.TileWidth / trick.TileHeight : 16.0 / 9;
        var n = Math.Max(1, (int)Math.Ceiling(_width / (StripHeight * aspect)));
        var tiles = new List<(int Col, TrickplayTile Tile)>(n);
        for (var i = 0; i < n; i++)
        {
            Film.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (trick.FindNearest(_mediaStart + _view.Start + (i + 0.5) / n * _view.Length) is { } tile) tiles.Add((i, tile));
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
            var image = new Image { Source = source, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
            Grid.SetColumn(image, col);
            Film.Children.Add(image);
        }
    }

    private void RebuildLane()
    {
        Lane.Children.Clear();
        if (_view.Duration <= 0 || _width <= 0) return;
        foreach (var m in _markers)
        {
            if (_visibleKinds is not null && !_visibleKinds.Contains(m.Kind)) continue;
            if (!MarkerKinds.ShowOnLane(m.Kind)) continue;
            var end = m.End ?? m.Start;
            if (end < _view.Start || m.Start > _view.End) continue;            // outside the zoomed window
            var x = XOf(m.Start);
            if (m.End is { } e)
            {
                var x0 = Math.Max(0, x);
                var bar = new Rectangle
                {
                    Width = Math.Max(6, Math.Min(_width, XOf(e)) - x0), Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = MarkerIcons.Brush(m.Kind, 200), IsHitTestVisible = false,
                };
                Canvas.SetLeft(bar, x0);
                Canvas.SetTop(bar, 22);
                Lane.Children.Add(bar);
            }
            if (m.Start < _view.Start) continue;                                // a range that began before the window: bar only
            var icon = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Tag = m,
                Background = MarkerIcons.Brush(m.Kind, 70), BorderBrush = MarkerIcons.Brush(m.Kind, 220), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = m.Glyph, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            AutomationProperties.SetAutomationId(icon, "TimelineMarker");
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

    // ---- zoom -----------------------------------------------------------------------------------------------------

    private void ZoomBy(double factor, double anchorSeconds)
    {
        if (!_view.ZoomAt(anchorSeconds, factor)) return;
        _userMovedAt = Stopwatch.GetTimestamp();
        Redraw(force: false, trickplayChanged: false);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomIn();

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOut();

    private void Strip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_view.IsZoomed) return;
        _view.Reset();
        Redraw(force: false, trickplayChanged: false);
        e.Handled = true;
    }

    private void Strip_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Strip);
        var delta = p.Properties.MouseWheelDelta / 120.0;
        if (delta == 0 || _view.Duration <= 0) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl) ZoomBy(Math.Pow(TimelineView.StepFactor, delta), SecondsAt(p.Position.X));
        else if (_view.IsZoomed && _view.ScrollBy((p.Properties.IsHorizontalMouseWheel ? delta : -delta) * _view.Length * 0.15))
        {
            _userMovedAt = Stopwatch.GetTimestamp();
            Redraw(force: false, trickplayChanged: false);
        }
        else return;
        ShowHover(p.Position.X);
        e.Handled = true;
    }

    private void ViewScroll_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingScroll || !_view.ScrollTo(e.NewValue)) return;
        _userMovedAt = Stopwatch.GetTimestamp();
        Redraw(force: false, trickplayChanged: false);
    }

    // ---- pointer --------------------------------------------------------------------------------------------------

    private void Strip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Strip);
        if (!p.Properties.IsLeftButtonPressed || _view.Duration <= 0) return;
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
        if (_view.Duration <= 0 || _width <= 0) return;
        x = Math.Clamp(x, 0, _width);
        var t = SecondsAt(x);
        Canvas.SetLeft(HoverLine, x);
        HoverLine.Visibility = Visibility.Visible;
        PreviewTime.Text = FrameTimecode.Position(t, Fps);
        Preview.HorizontalOffset = Math.Clamp(x - PreviewWidth / 2, 0, Math.Max(0, _width - PreviewWidth));
        Preview.VerticalOffset = -(180 + 8 + 24 + PreviewGap);
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
