// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Services;

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

    public LibraryPage()
    {
        InitializeComponent();
        GameList.ItemsSource = _shown;
    }

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
        if (_clipTiles.TryGetValue(folder, out var tile)) { tile.ThumbRequested = false; RequestThumb(tile); }
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
        _tiles = index is null || src is null ? [] : BuildTiles(index, src, _covers);

        var noLibrary = index is null || (index.Recordings.Count == 0 && src?.Problem is not null);
        GameScroll.Visibility = noLibrary ? Visibility.Collapsed : Visibility.Visible;
        NoLibrary.Visibility = noLibrary ? Visibility.Visible : Visibility.Collapsed;
        NoLibraryState.Message = src?.Problem ?? (AppServices.Steam.SteamFound
            ? "The first scan will list your recordings here."
            : "Steam wasn't found on this PC. Open Settings to point the app at a recordings folder.");

        ApplyGameFilter();
    }

    private static List<GameTile> BuildTiles(LibraryIndex index, ILibrarySource src, Dictionary<string, ImageSource?> covers)
    {
        var byGame = index.Recordings.ToLookup(r => r.GameId);
        var tiles = new List<GameTile>();
        var seen = new HashSet<string>();

        foreach (var g in index.Games)
        {
            seen.Add(g.GameId);
            tiles.Add(MakeTile(g.GameId, g.Name, g, byGame[g.GameId].ToList(), src, covers));
        }
        foreach (var group in byGame.Where(x => x.Key is not null && !seen.Contains(x.Key)))
        {
            var name = ulong.TryParse(group.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? GameNameResolver.Fallback(id) : "Unknown game";
            tiles.Add(MakeTile(group.Key!, name, null, group.ToList(), src, covers));
        }
        var loose = byGame[null].ToList();
        if (loose.Count > 0) tiles.Add(MakeTile(null, "Downloaded clips", null, loose, src, covers));

        return tiles.OrderByDescending(t => t.LastUtc).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static GameTile MakeTile(string? id, string name, GameEntry? entry, List<RecordingEntry> recs, ILibrarySource src, Dictionary<string, ImageSource?> covers)
    {
        // one BitmapImage per file, however often the index refreshes
        var path = src.ResolveArt(entry?.Art?.Cover);
        ImageSource? cover = null;
        if (path is not null && !covers.TryGetValue(path, out cover)) covers[path] = cover = Fmt.Image(path);
        return MakeTile(id, name, entry, recs, cover, path ?? "");
    }

    private static GameTile MakeTile(string? id, string name, GameEntry? entry, List<RecordingEntry> recs, ImageSource? cover, string coverPath) => new()
    {
        GameId = id,
        Name = name,
        Entry = entry,
        Cover = cover,
        CoverPath = coverPath,
        Clips = recs.Count(r => r.Kind != RecordingKind.CacheSession),
        Sessions = recs.Count(r => r.Kind == RecordingKind.CacheSession),
        SizeBytes = recs.Sum(r => r.SizeBytes),
        LastUtc = recs.Count == 0 ? DateTimeOffset.MinValue : recs.Max(r => r.StartUtc + TimeSpan.FromSeconds(Fmt.Duration(r))),
    };

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyGameFilter();

    private void ApplyGameFilter()
    {
        var q = SearchBox.Text.Trim();
        var list = q.Length == 0 ? _tiles : _tiles.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

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

    /// <summary>Makes the rail show <paramref name="list"/> with the fewest changes: unchanged tiles stay as they are, so the scroll position survives a refresh.</summary>
    private void SyncShown(List<GameTile> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var want = list[i];
            if (i < _shown.Count && _shown[i].Key == want.Key)
            {
                if (_shown[i].Sig != want.Sig) _shown[i] = want;
                continue;
            }
            var j = -1;
            for (var k = i + 1; k < _shown.Count; k++) if (_shown[k].Key == want.Key) { j = k; break; }
            if (j >= 0)
            {
                _shown.Move(j, i);
                if (_shown[i].Sig != want.Sig) _shown[i] = want;
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

        // hero: Steam's blurred hero art + logo when we have them, brand gradient otherwise
        var art = tile.Entry?.Art;
        HeroImage.Source = Fmt.Image(src.ResolveArt(art?.HeroBlur ?? art?.Hero));
        var logo = Fmt.Image(src.ResolveArt(art?.Logo));
        LogoImage.Source = logo;
        LogoImage.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
        GameTitle.Text = tile.Name;
        GameTitle.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
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
        foreach (var t in _clips) RequestThumb(t);
        if (resetScroll) GameScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static string RecSig(RecordingEntry r) => string.Create(CultureInfo.InvariantCulture,
        $"{r.RelativeFolder}|{r.SizeBytes}|{r.DurationSeconds}|{r.MarkerCount}|{r.Live}|{r.Title}|{r.Runs.Count}|{(r.Runs.Count > 0 ? r.Runs[^1].Last : 0)}");

    /// <summary>Loads the first tile of the clip's trickplay sheet as its thumbnail, if the sheet exists yet (else the gradient placeholder stays).</summary>
    private async void RequestThumb(ClipTile tile)
    {
        if (tile.Thumb is not null || tile.ThumbRequested) return;
        var src = _wired;
        if (src is null) return;
        tile.ThumbRequested = true;
        try
        {
            var index = await Task.Run(() => src.TrickplayFor(tile.Key)).ConfigureAwait(true);
            if (index is null) { tile.ThumbRequested = false; return; }
            var img = await ThumbnailLoader.FirstTileAsync(index);
            if (img is not null) tile.Thumb = img; else tile.ThumbRequested = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { tile.ThumbRequested = false; }
    }

    // =====================================================================================
    //  cache strip
    // =====================================================================================

    private void CacheHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCache();

    private void DrawCache()
    {
        foreach (var s in _pulses) s.Stop();
        _pulses.Clear();
        StripCanvas.Children.Clear();

        var empty = _sessions.Count == 0;
        CacheEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        StripCanvas.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        var live = _sessions.Any(s => s.Live);
        LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (live) Pulse(LiveDot);
        if (empty) return;

        var w = CacheHost.ActualWidth;
        if (w < 80) return;
        StripCanvas.Width = w;

        var now = DateTimeOffset.UtcNow;
        var keep = AppServices.Steam.MaxKeep ?? TimeSpan.FromHours(2);
        var firstStart = _sessions.Min(s => s.StartUtc);
        var span = TimeSpan.FromSeconds(Math.Max(keep.TotalSeconds, (now - firstStart).TotalSeconds + 60));
        var axisStart = now - span;
        double X(DateTimeOffset t) => Math.Clamp((t - axisStart).TotalSeconds / span.TotalSeconds, 0, 1) * w;

        const double top = 6, barH = 36;

        // the track is the whole window Steam may keep; whatever is not a session is a gap
        StripCanvas.Children.Add(new Rectangle
        {
            Width = w, Height = barH, RadiusX = 8, RadiusY = 8,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0x80, 0x80, 0x80)), Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80)), StrokeThickness = 1,
        });
        Place(StripCanvas.Children[^1], 0, top);

        // hour ticks, labelled below
        for (var h = 0; h * 3600 <= span.TotalSeconds; h++)
        {
            var x = X(now - TimeSpan.FromHours(h));
            if (h > 0 && x > 1)
            {
                var line = new Rectangle { Width = 1, Height = barH, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80)) };
                Place(line, x, top);
                StripCanvas.Children.Add(line);
            }
            var label = new TextBlock { Text = h == 0 ? "now" : $"−{h} h", Style = (Style)Application.Current.Resources["Caption"], FontSize = 11 };
            Place(label, h == 0 ? w - 26 : Math.Max(0, x - 12), 68);
            StripCanvas.Children.Add(label);
        }

        var n = 0;
        foreach (var s in _sessions)
        {
            n++;
            var dur = s.Live ? (now - s.StartUtc).TotalSeconds : Fmt.Duration(s);
            var x0 = X(s.StartUtc);
            var x1 = X(s.StartUtc + TimeSpan.FromSeconds(dur));
            var bw = Math.Max(8, x1 - x0);

            var bar = new Border
            {
                Width = bw, Height = barH, CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["BrandGradientH"],
                Tag = s,
            };
            var text = new TextBlock
            {
                Text = bw > 150 ? $"session {n} · {Fmt.Timecode(dur)}" : bw > 60 ? $"#{n}" : "",
                Foreground = new SolidColorBrush(Colors.White), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (s.Live && bw > 60)
            {
                var grid = new Grid();
                grid.Children.Add(text);
                grid.Children.Add(new TextBlock
                {
                    Text = "▶ live", Foreground = new SolidColorBrush(Colors.White), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 28, 0),
                });
                bar.Child = grid;
            }
            else bar.Child = text;
            ToolTipService.SetToolTip(bar, $"{Fmt.DateTime(s.StartUtc)} · {Fmt.Timecode(dur)}" + (s.Live ? " · live" : ""));
            bar.Tapped += Session_Tapped;
            Place(bar, x0, top);
            StripCanvas.Children.Add(bar);

            if (s.Live)
            {
                var dot = new Ellipse { Width = 9, Height = 9, Fill = (Brush)Application.Current.Resources["LiveBrush"], Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5 };
                Place(dot, Math.Min(w - 12, x0 + bw - 12), top + barH / 2 - 4.5);
                StripCanvas.Children.Add(dot);
                Pulse(dot);
            }

            // markers sit in their own lane under the bar, at their exact offset
            foreach (var m in AppServices.Library?.MarkersFor(s) ?? [])
            {
                var mx = X(s.StartUtc + TimeSpan.FromSeconds(m.OffsetSeconds));
                var glyph = new TextBlock { Text = m.Glyph, FontSize = 14, Tag = s };
                ToolTipService.SetToolTip(glyph, $"{m.Label} · {Fmt.Timecode(m.OffsetSeconds)}");
                Place(glyph, mx - 9, top + barH + 6);
                StripCanvas.Children.Add(glyph);
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

    private void Session_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RecordingEntry r }) Open(r.RelativeFolder);
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
