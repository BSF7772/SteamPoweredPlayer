// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Services;
using SteamPoweredPlayer.Views.Player;

namespace SteamPoweredPlayer.Views;

/// <summary>Games rail, hero, Cache strip, Clips grid, Recordings and Exports (plan §4.2).</summary>
public sealed partial class LibraryPage : Page
{
    private ILibrarySource? _wired;
    private List<GameTile> _tiles = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<GameTile> _shown = [];
    private string? _selectedKey;
    private List<RecordingEntry> _sessions = [];
    private List<ClipTile> _clips = [];
    private readonly List<Storyboard> _pulses = [];
    private readonly Dictionary<string, ImageSource?> _covers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClipTile> _clipTiles = new(StringComparer.Ordinal);
    private bool _suppressSelection;
    private string _shownSig = "";
    private string _clipsSig = "";
    private List<ClipTile> _shownClips = [];
    private string? _heroPath, _logoPath;

    /// <summary>Clip card thumbnails are decoded at about the card's width (logical px; the grid stretches cards from 216 px).</summary>
    private const double ClipThumbWidth = 360;
    private const double CoverWidth = 48;

    public LibraryPage()
    {
        InitializeComponent();
        GameList.ItemsSource = _shown;
        ClipRepeater.ElementPrepared += ClipRepeater_ElementPrepared;
    }

    private double DpiScale => XamlRoot?.RasterizationScale ?? 1.0;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Unwire();
        _wired = AppServices.Library;
        if (_wired is not null)
        {
            _wired.IndexChanged += Source_IndexChanged;
            _wired.DerivedChanged += Source_DerivedChanged;
        }
        _shownSig = "";
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Unwire();
    }

    private void Unwire()
    {
        if (_wired is null) return;
        _wired.IndexChanged -= Source_IndexChanged;
        _wired.DerivedChanged -= Source_DerivedChanged;
        _wired = null;
    }

    // Both events arrive on the UI thread (the source marshals them).
    private void Source_IndexChanged(object? sender, IndexChangedArgs e) => Refresh();

    private void Source_DerivedChanged(object? sender, string folder)
    {
        // markers arrived for a session, or trickplay for a clip: redraw only that part
        if (_sessions.Any(x => x.RelativeFolder == folder)) DrawCache();
        if (_clipTiles.TryGetValue(folder, out var tile)) RequestThumb(tile, trickplayChanged: true);
    }

    private void FirstRun_Confirmed(object? sender, EventArgs e)
    {
        // the first scan is already running (also after "choose recordings folder", which recreates the source)
        OnNavigatedTo(null!);
    }

    // =====================================================================================
    //  data -> rail
    // =====================================================================================

    private void Refresh()
    {
        var first = AppServices.NeedsFirstRun;
        FirstRun.Visibility = first ? Visibility.Visible : Visibility.Collapsed;
        LibraryRoot.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        if (first) return;

        var src = AppServices.Library;
        var index = src?.Current;
        _tiles = index is null || src is null ? [] : BuildTiles(index, src, _covers, DpiScale);

        var noLibrary = index is null || (index.Recordings.Count == 0 && src?.Problem is not null);
        GameScroll.Visibility = noLibrary ? Visibility.Collapsed : Visibility.Visible;
        NoLibrary.Visibility = noLibrary ? Visibility.Visible : Visibility.Collapsed;
        NoLibraryState.Message = src?.Problem ?? (AppServices.Steam.SteamFound
            ? "The first scan will list your recordings here."
            : "Steam wasn't found on this PC. Open Settings to point the app at a recordings folder.");

        ApplyGameFilter();
    }

    private static List<GameTile> BuildTiles(LibraryIndex index, ILibrarySource src, Dictionary<string, ImageSource?> covers, double scale)
    {
        var byGame = index.Recordings.ToLookup(r => r.GameId);
        var tiles = new List<GameTile>();
        var seen = new HashSet<string>();

        foreach (var g in index.Games)
        {
            seen.Add(g.GameId);
            tiles.Add(MakeTile(g.GameId, g.Name, g, byGame[g.GameId].ToList(), src, covers, scale));
        }
        foreach (var group in byGame.Where(x => x.Key is not null && !seen.Contains(x.Key)))
        {
            var name = ulong.TryParse(group.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? GameNameResolver.Fallback(id) : "Unknown game";
            tiles.Add(MakeTile(group.Key!, name, null, group.ToList(), src, covers, scale));
        }
        var loose = byGame[null].ToList();
        if (loose.Count > 0) tiles.Add(MakeTile(null, "Downloaded clips", null, loose, src, covers, scale));

        return tiles.OrderByDescending(t => t.LastUtc).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static GameTile MakeTile(string? id, string name, GameEntry? entry, List<RecordingEntry> recs, ILibrarySource src,
        Dictionary<string, ImageSource?> covers, double scale)
    {
        // one BitmapImage per file, however often the index refreshes; decoded at the rail's size (F8)
        var path = src.ResolveArt(entry?.Art?.Cover);
        ImageSource? cover = null;
        if (path is not null && !covers.TryGetValue(path, out cover)) covers[path] = cover = Fmt.Image(path, CoverWidth, scale);
        return GameTile.Create(id, name, entry, cover, path ?? "",
            clips: recs.Count(r => r.Kind != RecordingKind.CacheSession),
            sessions: recs.Count(r => r.Kind == RecordingKind.CacheSession),
            sizeBytes: recs.Sum(r => r.SizeBytes),
            lastUtc: recs.Count == 0 ? DateTimeOffset.MinValue : recs.Max(r => r.StartUtc + TimeSpan.FromSeconds(Fmt.Duration(r))));
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyGameFilter();

    private void ApplyGameFilter()
    {
        var q = SearchBox.Text.Trim();
        var list = q.Length == 0 ? _tiles.ToList() : _tiles.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        _suppressSelection = true;
        try
        {
            SyncShown(list);
            GameCountBadge.Value = _tiles.Count;

            RailEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RailEmptyState.Message = _tiles.Count == 0 ? "Games appear here after the first scan." : "No game matches your search.";

            var keep = _shown.FirstOrDefault(t => t.Key == _selectedKey) ?? _shown.FirstOrDefault();
            if (!ReferenceEquals(GameList.SelectedItem, keep)) GameList.SelectedItem = keep;
            if (keep is null) { _selectedKey = null; AppServices.PreferredGameId = null; _shownSig = ""; return; }
            var changed = keep.Key != _selectedKey;
            _selectedKey = keep.Key;
            AppServices.PreferredGameId = keep.GameId;
            if (changed) AppServices.Trickplay?.Kick();
            ShowGame(keep, resetScroll: changed);
        }
        finally { _suppressSelection = false; }
    }

    /// <summary>
    /// Makes the rail show <paramref name="list"/> with the fewest changes (F11): a game that is already shown keeps its row object and
    /// only its numbers change (no remove + add, so no entrance animation); rows are inserted or removed only when the set of games
    /// changes, and moved only when the order really changes.
    /// </summary>
    private void SyncShown(List<GameTile> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var want = list[i];
            if (i < _shown.Count && _shown[i].Key == want.Key) { _shown[i].Update(want); list[i] = _shown[i]; continue; }
            var j = -1;
            for (var k = i + 1; k < _shown.Count; k++) if (_shown[k].Key == want.Key) { j = k; break; }
            if (j >= 0)
            {
                _shown.Move(j, i);
                _shown[i].Update(want);
                list[i] = _shown[i];
            }
            else _shown.Insert(i, want);
        }
        while (_shown.Count > list.Count) _shown.RemoveAt(_shown.Count - 1);
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (GameList.SelectedItem is GameTile t)
        {
            _selectedKey = t.Key;
            AppServices.PreferredGameId = t.GameId;
            AppServices.Trickplay?.Kick();
            ShowGame(t, resetScroll: true);
        }
    }

    // =====================================================================================
    //  selected game
    // =====================================================================================

    private void ShowGame(GameTile tile, bool resetScroll)
    {
        var src = AppServices.Library;
        var index = src?.Current;
        if (src is null || index is null) return;
        var recs = index.Recordings.Where(r => r.GameId == tile.GameId).ToList();

        // An index refresh that did not touch this game changes nothing on screen.
        var sig = tile.Sig + "#" + string.Join(';', recs.Select(RecSig)) + "#" + (tile.Entry?.Art is { } a0 ? $"{a0.Hero}|{a0.HeroBlur}|{a0.Logo}" : "");
        if (!resetScroll && sig == _shownSig) return;
        _shownSig = sig;

        // hero (F8): Steam's full-resolution hero art (1920 px), not the 192 px blurred one, decoded at the card's width; brand
        // gradient when there is none. Same file as before: the image is kept (no reload, no flicker on a live refresh).
        var art = tile.Entry?.Art;
        var heroPath = src.ResolveArt(art?.Hero ?? art?.HeroBlur);
        if (heroPath != _heroPath)
        {
            _heroPath = heroPath;
            HeroImage.Source = Fmt.Image(heroPath, Math.Max(GameScroll.ActualWidth, 1200), DpiScale);
        }
        var logoPath = src.ResolveArt(art?.Logo);
        if (logoPath != _logoPath)
        {
            _logoPath = logoPath;
            LogoImage.Source = Fmt.Image(logoPath, LogoImage.MaxWidth, DpiScale);
        }
        var hasLogo = LogoImage.Source is not null;
        LogoImage.Visibility = hasLogo ? Visibility.Visible : Visibility.Collapsed;
        GameTitle.Text = tile.Name;
        GameTitle.Visibility = hasLogo ? Visibility.Collapsed : Visibility.Visible;
        GameSubtitle.Text = tile.LastUtc == DateTimeOffset.MinValue ? "Nothing recorded yet" : "Last recorded " + Fmt.Ago(tile.LastUtc);
        HeroClips.Text = tile.Sessions > 0 ? tile.Caption : Fmt.Plural(tile.Clips, "clip");
        HeroSize.Text = Fmt.Bytes(tile.SizeBytes);

        _sessions = recs.Where(r => r.Kind == RecordingKind.CacheSession).OrderBy(r => r.StartUtc).ToList();
        var keep = AppServices.Steam.MaxKeep;
        CacheCaption.Text = keep is { } k ? $"temporary · Steam keeps the last {Fmt.Span(k)}" : "temporary · Steam trims it on its own";
        DrawCache();

        // keep the tile objects (and the thumbnails they hold) of clips that did not change
        var clips = recs.Where(r => r.Kind != RecordingKind.CacheSession).ToList();
        var oldTiles = new Dictionary<string, ClipTile>(_clipTiles);
        _clipTiles.Clear();
        _clips = [];
        foreach (var r in clips)
        {
            var t = oldTiles.TryGetValue(r.RelativeFolder, out var old) && RecSig(old.Entry) == RecSig(r) ? old : new ClipTile { Entry = r };
            _clipTiles[r.RelativeFolder] = t;
            _clips.Add(t);
        }
        var clipsSig = string.Join(';', clips.Select(RecSig));
        if (resetScroll || clipsSig != _clipsSig)
        {
            _clipsSig = clipsSig;
            var offset = GameScroll.VerticalOffset;
            ApplyClips();
            if (!resetScroll) { GameScroll.UpdateLayout(); GameScroll.ChangeView(null, offset, null, disableAnimation: true); }
        }
        if (resetScroll) GameScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static string RecSig(RecordingEntry r) => string.Create(CultureInfo.InvariantCulture,
        $"{r.RelativeFolder}|{r.SizeBytes}|{r.DurationSeconds}|{r.MarkerCount}|{r.Live}|{r.Title}|{r.Runs.Count}|{(r.Runs.Count > 0 ? r.Runs[^1].Last : 0)}");

    /// <summary>A card was realised (the grid virtualises): load its thumbnail now, not for every clip of the game up front.</summary>
    private void ClipRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index >= 0 && args.Index < _shownClips.Count) RequestThumb(_shownClips[args.Index]);
    }

    /// <summary>
    /// F7: the card shows Steam's own <c>thumbnail.jpg</c> of a saved clip (1080p, read-only with delete sharing), decoded at the card's
    /// width × DPI; else the first tile of the clip's trickplay sheet, if it exists yet (else the gradient placeholder stays). Same
    /// helper as the Player's playlist.
    /// </summary>
    private async void RequestThumb(ClipTile tile, bool trickplayChanged = false)
    {
        if (tile.ThumbRequested || tile.ThumbFromSteam || (tile.Thumb is not null && !trickplayChanged)) return;
        var src = _wired;
        if (src is null) return;
        tile.ThumbRequested = true;
        try
        {
            if (!trickplayChanged)
            {
                var folder = src.GetMedia(tile.Key)?.Folder
                    ?? (AppServices.Steam.RecordingsPath is { } root ? System.IO.Path.Combine(root, tile.Key.Replace('/', System.IO.Path.DirectorySeparatorChar)) : null);
                var jpg = await Task.Run(() => PlaylistThumbs.ReadSteamThumbnail(tile.Key, folder)).ConfigureAwait(true);
                if (jpg is not null && await PlaylistThumbs.DecodeAsync(jpg, (int)Math.Ceiling(ClipThumbWidth * DpiScale)) is { } image)
                {
                    tile.ThumbKeepAlive = image.KeepAlive;
                    tile.ThumbFromSteam = true;
                    tile.Thumb = image.Source;
                    return;
                }
            }
            var index = await Task.Run(() => src.TrickplayFor(tile.Key)).ConfigureAwait(true);
            if (index is null) return;
            if (await ThumbnailLoader.FirstTileAsync(index) is { } img && !tile.ThumbFromSteam) tile.Thumb = img;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        finally { tile.ThumbRequested = false; }
    }

    // =====================================================================================
    //  cache strip
    // =====================================================================================

    private void CacheHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCache();

    /// <summary>The wall-clock span of the chunks on disk: chunk N of a cache session starts (N − 1) · S after the folder's start time.</summary>
    private static StripSpan SpanOf(RecordingEntry s)
    {
        if (s.Runs.Count == 0) return new(s.StartUtc, s.StartUtc + TimeSpan.FromSeconds(Fmt.Duration(s)));
        return new(s.StartUtc + TimeSpan.FromSeconds((s.Runs[0].First - 1) * s.SegmentSeconds),
                   s.StartUtc + TimeSpan.FromSeconds(s.Runs[^1].Last * s.SegmentSeconds));
    }

    private static string Clock(DateTimeOffset utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// F9: every cache session is a block on an absolute time axis (local times; the date when the span crosses midnight), gaps between
    /// sessions stay visible, long gaps (days) are drawn as a short break. Each block is a button (<c>LibraryCacheBlock</c>) that opens it.
    /// </summary>
    private void DrawCache()
    {
        foreach (var s in _pulses) s.Stop();
        _pulses.Clear();
        StripCanvas.Children.Clear();

        var empty = _sessions.Count == 0;
        CacheEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        StripCanvas.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ReplayButton.IsEnabled = !empty;
        var live = _sessions.Any(s => s.Live);
        LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (live) Pulse(LiveDot);
        if (empty) return;

        var w = CacheHost.ActualWidth;
        if (w < 80) return;
        StripCanvas.Width = w;

        var spans = _sessions.Select(SpanOf).ToList();
        var layout = CacheStripLayout.Build(spans, w, TimeZoneInfo.Local, DateTimeOffset.UtcNow);
        const double top = 18, barH = 36;
        var dim = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80));
        var caption = (Style)Application.Current.Resources["Caption"];

        // the track behind the blocks: whatever is not a block is a gap
        var track = new Rectangle
        {
            Width = w, Height = barH, RadiusX = 8, RadiusY = 8,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0x80, 0x80, 0x80)), Stroke = dim, StrokeThickness = 1,
        };
        Place(track, 0, top);
        StripCanvas.Children.Add(track);

        // L1: everything older than 24 h is squeezed into the left quarter; the break says so
        if (layout.Cutoff is { } cut)
        {
            var brk = new Rectangle { Width = cut.X1 - cut.X0, Height = barH + 12, Fill = dim, RadiusX = 3, RadiusY = 3 };
            ToolTipService.SetToolTip(brk, "Older recordings (more than 24 hours ago) are squeezed into this part; the last 24 hours are on the right");
            Place(brk, cut.X0, top - 6);
            StripCanvas.Children.Add(brk);
            var older = new TextBlock { Text = "older", Style = caption, FontSize = 10, Opacity = 0.7 };
            Place(older, Math.Max(0, cut.X0 - 30), 0);
            StripCanvas.Children.Add(older);
            var last = new TextBlock { Text = "last 24 h", Style = caption, FontSize = 10, Opacity = 0.7 };
            Place(last, cut.X1 + 4, 0);
            StripCanvas.Children.Add(last);
        }
        if (layout.RecentEmpty)
        {
            var x0 = layout.Cutoff?.X1 ?? 0;
            var note = new TextBlock
            {
                Text = "Nothing recorded in the last 24 h", Style = caption, Width = Math.Max(0, w - x0), TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
            };
            Place(note, x0, top + barH / 2 - 8);
            StripCanvas.Children.Add(note);
        }

        // midnights: a dashed line with the new day's name, so "yesterday 23:00" and "today 07:00" are never confused
        foreach (var m in layout.Midnights)
        {
            var line = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, X2 = 0, Y1 = 0, Y2 = barH + 10, Stroke = dim, StrokeThickness = 1, StrokeDashArray = [3, 3], IsHitTestVisible = false,
            };
            Place(line, Math.Clamp(m.X, 0, w - 1), top - 4);
            StripCanvas.Children.Add(line);
            var ml = new TextBlock { Text = m.Label, Style = caption, FontSize = 10, Opacity = 0.8, IsHitTestVisible = false };
            ml.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(ml, Math.Clamp(m.X + 3, 0, Math.Max(0, w - ml.DesiredSize.Width)), 0);
            StripCanvas.Children.Add(ml);
        }

        // the axis ends at NOW
        var nowEdge = new Rectangle { Width = 2, Height = barH + 10, Fill = (Brush)Application.Current.Resources["BrandGradientH"], IsHitTestVisible = false };
        Place(nowEdge, Math.Clamp(layout.NowX - 2, 0, w - 2), top - 4);
        StripCanvas.Children.Add(nowEdge);

        foreach (var g in layout.Gaps.Where(g => g.Compressed))
        {
            var mark = new TextBlock { Text = "≈", Style = caption, FontSize = 16, Width = g.X1 - g.X0, TextAlignment = TextAlignment.Center };
            ToolTipService.SetToolTip(mark, "No recording for " + (g.Length.TotalDays >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{g.Length.TotalDays:0.#} days") : Fmt.Span(g.Length)));
            Place(mark, g.X0, top + barH / 2 - 12);
            StripCanvas.Children.Add(mark);
        }

        foreach (var b in layout.Blocks)
        {
            var s = _sessions[b.Index];
            var bw = Math.Max(2, b.X1 - b.X0);
            var dur = b.Span.Seconds;
            var range = $"{Fmt.DateTime(b.Span.StartUtc)} – {Clock(b.Span.EndUtc)}";

            var text = new TextBlock
            {
                Text = bw > 210 ? $"{Clock(b.Span.StartUtc)} – {Clock(b.Span.EndUtc)} · {Fmt.Timecode(dur)}" : bw > 70 ? Fmt.Timecode(dur) : "",
                Foreground = new SolidColorBrush(Colors.White), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            var face = new Border { CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["BrandGradientH"] };
            if (s.Live && bw > 120)
            {
                var grid = new Grid();
                grid.Children.Add(text);
                grid.Children.Add(new TextBlock
                {
                    Text = "▶ live", Foreground = new SolidColorBrush(Colors.White), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 28, 0),
                });
                face.Child = grid;
            }
            else face.Child = text;

            // A real button: keyboard and UI Automation can open the block (the --uicheck invokes it by AutomationId and Tag).
            var bar = new Button
            {
                Width = bw, Height = barH, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Colors.Transparent), MinWidth = 0, MinHeight = 0,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = face, Tag = s,
            };
            AutomationProperties.SetAutomationId(bar, "LibraryCacheBlock");
            AutomationProperties.SetName(bar, $"Cache session {range}" + (s.Live ? ", recording now" : ""));
            ToolTipService.SetToolTip(bar, $"{range} · {Fmt.Timecode(dur)}" + (s.Live ? " · live" : ""));
            bar.Click += Session_Click;
            Place(bar, b.X0, top);
            StripCanvas.Children.Add(bar);

            if (s.Live)
            {
                var dot = new Ellipse
                {
                    Width = 9, Height = 9, Fill = (Brush)Application.Current.Resources["LiveBrush"], Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 1.5, IsHitTestVisible = false,
                };
                Place(dot, Math.Max(b.X0 + 2, b.X1 - 14), top + barH / 2 - 4.5);
                StripCanvas.Children.Add(dot);
                Pulse(dot);
            }

            // markers sit in their own lane under the block, at their wall-clock time (marker offsets are on the tfdt axis)
            var shift = AppServices.Library?.GetMedia(s.RelativeFolder)?.AxisShiftSeconds ?? 0;
            foreach (var m in AppServices.Library?.MarkersFor(s) ?? [])
            {
                var mx = CacheStripLayout.XOf(b, s.StartUtc + TimeSpan.FromSeconds(m.OffsetSeconds - shift));
                var glyph = new TextBlock { Text = m.Glyph, FontSize = 14, Tag = s };
                ToolTipService.SetToolTip(glyph, $"{m.Label} · {Clock(s.StartUtc + TimeSpan.FromSeconds(m.OffsetSeconds - shift))}");
                Place(glyph, mx - 9, top + barH + 6);
                StripCanvas.Children.Add(glyph);
            }
        }

        // absolute times under the axis: each block's start and end, round times inside long blocks
        foreach (var t in layout.Ticks)
        {
            var tick = new Rectangle { Width = 1, Height = 5, Fill = dim };
            Place(tick, Math.Clamp(t.X, 0, w - 1), top + barH);
            StripCanvas.Children.Add(tick);
            var label = new TextBlock { Text = t.Label, Style = caption, FontSize = 11 };
            if (t.Label == "Now") label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var lw = label.DesiredSize.Width;
            var rel = t.Relative is null ? null : new TextBlock { Text = t.Relative, Style = caption, FontSize = 10, Opacity = 0.65 };
            rel?.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var cw = Math.Max(lw, rel?.DesiredSize.Width ?? 0);              // the two lines share one centre
            var cx = Math.Clamp(t.X - cw / 2, 0, Math.Max(0, w - cw));
            Place(label, cx + (cw - lw) / 2, top + barH + 12);
            StripCanvas.Children.Add(label);
            if (rel is not null)
            {
                Place(rel, cx + (cw - rel.DesiredSize.Width) / 2, top + barH + 26);
                StripCanvas.Children.Add(rel);
            }
        }
    }

    private static void Place(UIElement el, double x, double y)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
    }

    private void Pulse(UIElement el)
    {
        var a = new DoubleAnimation
        {
            From = 1, To = 0.25, Duration = TimeSpan.FromSeconds(0.9), AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(a, el);
        Storyboard.SetTargetProperty(a, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
        _pulses.Add(sb);
    }

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RecordingEntry r }) Open(r.RelativeFolder);
    }

    /// <summary>Replays the cache: the session that is recording now, else the newest one.</summary>
    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        var s = _sessions.FirstOrDefault(x => x.Live) ?? _sessions.LastOrDefault();
        if (s is not null) Open(s.RelativeFolder);
    }

    // =====================================================================================
    //  clips
    // =====================================================================================

    private void ClipFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ApplyClips();
    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyClips();

    private void ApplyClips()
    {
        if (ClipFilter is null || SortBox is null || ClipRepeater is null) return;
        var tag = ClipFilter.SelectedItem?.Tag as string ?? "all";
        IEnumerable<ClipTile> q = tag switch
        {
            "saved" => _clips.Where(c => !c.Downloaded),
            "downloaded" => _clips.Where(c => c.Downloaded),
            "starred" => [],                       // stars are kept by the app later; nothing is starred yet
            _ => _clips,
        };
        q = SortBox.SelectedIndex switch
        {
            1 => q.OrderBy(c => c.Entry.StartUtc),
            2 => q.OrderByDescending(c => c.Seconds),
            3 => q.OrderByDescending(c => c.Entry.MarkerCount).ThenByDescending(c => c.Entry.StartUtc),
            _ => q.OrderByDescending(c => c.Entry.StartUtc),
        };
        var list = q.ToList();
        _shownClips = list;
        ClipRepeater.ItemsSource = list;
        ClipCount.Text = tag == "all" ? $"· {_clips.Count}" : $"· {list.Count} of {_clips.Count}";

        var none = list.Count == 0;
        ClipsEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        ClipRepeater.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        if (none)
        {
            (ClipsEmpty.Title, ClipsEmpty.Message) = tag switch
            {
                "starred" => ("Nothing starred yet", "Star a clip to find it here quickly."),
                "downloaded" => ("No downloaded clips", "Clips you receive from other devices show up here."),
                "saved" => ("No saved clips", "Clips you save in Steam show up here."),
                _ => ("No clips here", "Save a clip in Steam, or from the Cache above, and it will appear here."),
            };
        }
    }

    private void Clip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipTile c }) Open(c.Key);
    }

    private static void Open(string recordingKey) => AppServices.Window?.NavigateToPlayer(new PlayerRequest(recordingKey));

    // =====================================================================================
    //  small helpers
    // =====================================================================================

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Pop((UIElement)sender, 1.03f);
    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Pop((UIElement)sender, 1f);

    private static void Pop(UIElement el, float scale)
    {
        el.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        el.CenterPoint = new Vector3(el.ActualSize.X / 2, el.ActualSize.Y / 2, 0);
        el.Scale = new Vector3(scale, scale, 1);
    }
}
