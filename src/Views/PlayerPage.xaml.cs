// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Services;
using SteamPoweredPlayer.Views.Player;
using VirtualKey = Windows.System.VirtualKey;

namespace SteamPoweredPlayer.Views;

/// <summary>
/// The Player page (plan §4.2): Playlist | video, timeline, transport | Events and Export. Navigation parameter: <see cref="PlayerRequest"/>
/// (<c>RecordingKey</c> = <c>RecordingEntry.RelativeFolder</c>; <c>StartSeconds</c> = UI time, 0 at the head).
/// </summary>
/// <remarks>
/// Everything mpv-related lives in <see cref="PlayerHost"/>; the page only translates between XAML and <see cref="PlaybackSession"/>.
/// mpv and session events arrive on mpv's thread and are marshalled to the dispatcher; position updates are coalesced into one pending
/// dispatcher item (no timer drives the playhead: mpv's <c>time-pos</c> fires per video frame while playing).
/// </remarks>
public sealed partial class PlayerPage : Page
{
    private static readonly double[] Speeds = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];
    private const double LiveEdgeSeconds = 8;          // within this of the live end counts as "live"
    private const double LiveStartBehindSeconds = 30;  // opening a live session starts this far behind its end

    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<EventRow> _eventRows = [];
    private readonly ObservableCollection<PlaylistRow> _playlist = [];
    private readonly TileCache _thumbTiles = new();
    private readonly ObservableCollection<UIElement> _chips = [];
    private readonly HashSet<MarkerKind> _visibleKinds = [];
    private readonly AbLoop _ab = new();
    private readonly Stopwatch _flushClock = Stopwatch.StartNew();
    private readonly Stopwatch _debugClock = Stopwatch.StartNew();

    private PlayerHost? _host;
    private PlaybackSession? _session;
    private DispatcherQueueTimer? _idleTimer, _pulseTimer, _focusTimer;
    private bool _compact;
    private AppWindow? _appWindow;
    private Window? _window;
    private string? _key;
    private RecordingEntry? _entry;
    private SessionMedia? _media;
    private TrickplayIndex? _trick;
    private long _trickQueriedAt;
    private List<LaneMarker> _lane = [];
    private IReadOnlyList<MarkerKind> _chipKinds = [];
    private string _eventSignature = "";
    private int _gen, _posQueued, _speedIndex = 3;
    private double _latestTimePos = double.NaN;
    private double _fps = FrameTimecode.FallbackFps;
    private double? _chunkFps;
    private string? _playlistGame;
    private int _playlistVersion;
    private bool _started, _attached, _cleaned, _subscribed, _fullscreen, _trickPaused, _overLower, _syncingVolume, _debug;

    public PlayerRequest? Request { get; private set; }

    public PlayerPage()
    {
        InitializeComponent();
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        EventsList.ItemsSource = _eventRows;
        PlaylistList.ItemsSource = _playlist;
        FilterChips.ItemsSource = _chips;
        BuildSpeedMenu();
        Lower.PointerEntered += (_, _) => _overLower = true;
        Lower.PointerExited += (_, _) => _overLower = false;
        Timeline.SeekRequested += Timeline_SeekRequested;
        Timeline.MarkerInvoked += m => JumpTo(m);
        Loaded += Page_Loaded;
        Unloaded += (_, _) => Cleanup();
    }

    // =====================================================================================
    //  lifetime
    // =====================================================================================

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Request = e.Parameter as PlayerRequest;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Cleanup();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        Focus(FocusState.Programmatic);
        _idleTimer = _ui.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(2);
        _idleTimer.IsRepeating = false;
        _idleTimer.Tick += (_, _) => { if (_fullscreen && !Timeline.IsScrubbing && !_overLower) HideChrome(); };
        _appWindow = AppServices.Window?.AppWindow;
        if (_appWindow is not null) _appWindow.Changed += AppWindow_Changed;
        if (AppServices.Window is { } win) { _window = win; win.Activated += Window_Activated; }

        var host = PlayerHost.Instance;
        if (host is null) { ShowStatus("Playback isn't available", "The player was not started.", icon: true); return; }
        ShowStatus("Starting the player…", null, busy: true);
        try { await host.Ready; }
        catch (Exception ex)
        {
            ShowStatus("Playback isn't available", ex.Message + "\nlibmpv-2.dll must sit next to the app (tools\\bootstrap.ps1 installs it).", icon: true);
            return;
        }
        if (_cleaned) return;

        _host = host;
        _session = host.Session!;
        try { host.AttachPanel(VideoPanel); _attached = true; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ShowStatus("Couldn't show the video", ex.Message, icon: true);
            return;
        }
        SyncSize();
        Subscribe();
        SyncVolumeUi();
        var key = Request?.RecordingKey;
        if (string.IsNullOrEmpty(key) || key == "sample") key = PickDemoKey();
        if (key is null) { ShowStatus("No recording to play", "Open one from the library.", icon: true); return; }
        await LoadAsync(key, Request?.StartSeconds);
    }

    /// <summary>For <c>--page player</c>: the newest finished clip of the library, else any recording.</summary>
    private static string? PickDemoKey()
    {
        var all = AppServices.Library?.Current?.Recordings;
        return (all?.Where(r => !r.Live && r.Kind == RecordingKind.Clip).OrderByDescending(r => r.StartUtc).FirstOrDefault()
                ?? all?.OrderByDescending(r => r.StartUtc).FirstOrDefault())?.RelativeFolder;
    }

    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        _gen++;
        SaveResume(flush: true);
        Unsubscribe();
        _idleTimer?.Stop();
        if (_appWindow is not null)
        {
            try { _appWindow.Changed -= AppWindow_Changed; }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { /* the window is closing */ }
            _appWindow = null;
        }
        if (_window is not null) { _window.Activated -= Window_Activated; _window = null; }
        if (_fullscreen) SetFullscreenNow(false);
        if (_trickPaused) { AppServices.Trickplay?.Resume(); _trickPaused = false; }
        _ab.Clear();
        ApplyAbLoop();                       // mpv keeps ab-loop-a/b across loads: never leave a loop for the next recording
        if (_attached && _host is not null) { _host.DetachPanel(VideoPanel); _attached = false; }
        if (_host is not null) _ = _host.StopAsync();
    }

    private void Subscribe()
    {
        if (_subscribed || _host is null || _session is null) return;
        _subscribed = true;
        var mpv = _host.Mpv!;
        mpv.PauseChanged += OnMpvPause;
        mpv.SpeedChanged += OnMpvSpeed;
        mpv.VolumeChanged += OnMpvVolume;
        mpv.MuteChanged += OnMpvMute;
        _session.PositionChanged += OnSessionPosition;
        _session.Reloaded += OnSessionReloaded;
        _session.ReachedEnd += OnSessionEnd;
        _session.ReloadFailed += OnSessionReloadFailed;
        _host.Follower!.Trimmed += OnTrimmed;
        _host.Metrics.Measured += OnMeasured;
        _host.DeviceLost += OnDeviceLost;
        if (AppServices.Library is { } lib)
        {
            lib.MediaChanged += OnLibraryMediaChanged;
            lib.DerivedChanged += OnLibraryDerivedChanged;
        }
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _host is null || _session is null) return;
        _subscribed = false;
        var mpv = _host.Mpv!;
        mpv.PauseChanged -= OnMpvPause;
        mpv.SpeedChanged -= OnMpvSpeed;
        mpv.VolumeChanged -= OnMpvVolume;
        mpv.MuteChanged -= OnMpvMute;
        _session.PositionChanged -= OnSessionPosition;
        _session.Reloaded -= OnSessionReloaded;
        _session.ReachedEnd -= OnSessionEnd;
        _session.ReloadFailed -= OnSessionReloadFailed;
        _host.Follower!.Trimmed -= OnTrimmed;
        _host.Metrics.Measured -= OnMeasured;
        _host.DeviceLost -= OnDeviceLost;
        if (AppServices.Library is { } lib)
        {
            lib.MediaChanged -= OnLibraryMediaChanged;
            lib.DerivedChanged -= OnLibraryDerivedChanged;
        }
    }

    // =====================================================================================
    //  loading a recording
    // =====================================================================================

    private async Task LoadAsync(string key, double? startUi)
    {
        if (_host is null || _session is null) return;
        var host = _host;
        var session = _session;
        var lib = AppServices.Library;
        var gen = ++_gen;

        if (_key is not null && _media is not null) SaveResume(flush: true);
        _key = key;
        _media = null;
        _ab.Clear();
        ApplyAbLoop();
        UpdateLoopUi();
        _entry = lib?.Current?.Recordings.FirstOrDefault(r => r.RelativeFolder == key);
        BuildPlaylist();
        ResetExportEdits();
        Timeline.ResetZoom();
        ClearEvents();
        ShowStatus("Opening…", null, busy: true);

        var media = lib?.GetMedia(key);
        if (media is null || media.ChunkCount == 0)
        {
            ShowStatus("This recording isn't available", "Steam may have removed it, or its files are not readable.", icon: true);
            Timeline.Configure(0, 0, [], null, []);
            TimeText.Text = FrameTimecode.PositionOfLength(0, 0, _fps);
            return;
        }

        var first = media.ChunkStartSeconds(media.FirstChunk!.Value);
        var last = media.ChunkEndSeconds(media.LastChunk!.Value);          // clips: on the tfdt axis (AxisShiftSeconds)
        double? startMedia = null;
        var resumed = false;
        if (startUi is { } s) startMedia = first + s;
        else if (ResumePositions.ResumeMediaSeconds(host.Positions.Get(key), first, last) is { } r) { startMedia = r; resumed = true; }
        else if (media.Live) startMedia = Math.Max(first, last - LiveStartBehindSeconds);

        _media = media;
        InitExportUi();
        UpdateExportUi();
        _chunkFps = null;
        if (!_trickPaused && AppServices.Trickplay is { } trick) { trick.Pause(); _trickPaused = true; }
        Task open;
        try { open = host.OpenAsync(media, startMedia, play: true); }
        catch (Exception ex) { ResumeTrickplay(); ShowStatus("Couldn't open this recording", ex.Message, icon: true); return; }

        // The session already knows the media now (OpenAsync set it before its first await): build the UI around it.
        _trick = lib?.TrickplayFor(key);
        _trickQueriedAt = Stopwatch.GetTimestamp();
        RefreshMarkers(resetFilter: true);
        ConfigureTimeline();
        UpdateLiveUi(startMedia ?? first);
        UpdateTime(startMedia is { } sm ? session.MediaToUi(sm) : 0);

        try
        {
            await open;
        }
        catch (OperationCanceledException) { ResumeTrickplay(); return; }
        catch (Exception ex) when (ex is MpvException or TimeoutException or InvalidOperationException)
        {
            ResumeTrickplay();
            if (gen == _gen) ShowStatus("Couldn't open this recording", ex.Message, icon: true);
            return;
        }
        ResumeTrickplay();
        if (gen != _gen) return;
        ClearStatus();
        var measured = await Task.Run(() => ChunkFrameRate.Measure(media));
        if (gen != _gen) return;
        _chunkFps = measured is { } rate ? FrameTimecode.Nominal(rate) : null;
        ReadFps();
        UpdatePlayGlyph();
        ApplyPosition();
        if (resumed) AppServices.Toast("Resumed", $"Continuing at {FrameTimecode.Position(session.UiPosition, _fps)}.", InfoBarSeverity.Informational, 3);
    }

    private void ResumeTrickplay()
    {
        if (!_trickPaused) return;
        _trickPaused = false;
        AppServices.Trickplay?.Resume();
    }

    // =====================================================================================
    //  status overlay
    // =====================================================================================

    private void ShowStatus(string text, string? hint, bool busy = false, bool icon = false)
    {
        StatusPanel.Visibility = Visibility.Visible;
        Ring.IsActive = busy;
        Ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = icon ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = text;
        StatusHint.Text = hint ?? "";
        StatusHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearStatus()
    {
        Ring.IsActive = false;
        StatusPanel.Visibility = Visibility.Collapsed;
    }

    // =====================================================================================
    //  markers, events, timeline
    // =====================================================================================

    private void RefreshMarkers(bool resetFilter)
    {
        if (_session is null || _media is null || _key is null) return;
        var all = AppServices.Library?.FullMarkersFor(_key) ?? [];
        var visible = Markers.Place(all, _media).Visible;
        _lane = [.. visible.Select(m =>
        {
            var kind = MarkerKinds.Classify(m.Entry);
            return new LaneMarker(m, kind, string.IsNullOrWhiteSpace(m.Entry.Title) ? MarkerIcons.Label(kind).TrimEnd('s') : m.Entry.Title!,
                _session.MediaToUi(m.StartSeconds), m.EndSeconds is { } e ? _session.MediaToUi(e) : null);
        })];

        var kinds = _lane.Select(m => m.Kind).Distinct().Order().ToList();
        if (resetFilter || !kinds.SequenceEqual(_chipKinds))
        {
            if (resetFilter) _visibleKinds.Clear();
            foreach (var k in kinds) if (resetFilter || !_chipKinds.Contains(k)) _visibleKinds.Add(k);
            _chipKinds = kinds;
            BuildChips();
        }
        RefreshEventRows();
    }

    private void ClearEvents()
    {
        _lane = [];
        _chipKinds = [];
        _visibleKinds.Clear();
        _eventSignature = "";
        _chips.Clear();
        _eventRows.Clear();
        EventBadge.Visibility = Visibility.Collapsed;
        EventsEmpty.Visibility = Visibility.Visible;
    }

    private void BuildChips()
    {
        _chips.Clear();
        if (_chipKinds.Count < 2) return;
        foreach (var kind in _chipKinds)
        {
            var count = _lane.Count(m => m.Kind == kind);
            var chip = new ToggleButton
            {
                Tag = kind, IsChecked = _visibleKinds.Contains(kind), Padding = new Thickness(10, 4, 10, 4), FontSize = 12, IsTabStop = false,
                Content = $"{MarkerIcons.Glyph(kind)} {MarkerIcons.Label(kind)} {count.ToString(CultureInfo.InvariantCulture)}",
            };
            chip.Click += Chip_Click;
            _chips.Add(chip);
        }
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: MarkerKind kind } chip) return;
        if (chip.IsChecked == true) _visibleKinds.Add(kind); else _visibleKinds.Remove(kind);
        _eventSignature = "";
        RefreshEventRows();
    }

    private bool Shown(LaneMarker m) => _chipKinds.Count < 2 || _visibleKinds.Contains(m.Kind);

    private void RefreshEventRows()
    {
        var shown = _lane.Where(Shown).ToList();
        var signature = string.Create(CultureInfo.InvariantCulture, $"{shown.Count}:{shown.FirstOrDefault()?.Start:0.0}:{shown.LastOrDefault()?.Start:0.0}");
        if (signature != _eventSignature)
        {
            _eventSignature = signature;
            _eventRows.Clear();
            foreach (var m in shown) _eventRows.Add(new EventRow { Marker = m });
        }
        EventBadge.Value = shown.Count;
        EventBadge.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EventsEmpty.Visibility = _lane.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Timeline.SetKindFilter(_chipKinds.Count < 2 ? null : new HashSet<MarkerKind>(_visibleKinds));
    }

    private void ConfigureTimeline()
    {
        if (_session?.Media is not { } media) return;
        var gaps = new List<(double, double)>();
        for (var i = 1; i < media.Runs.Count; i++)
            gaps.Add((_session.MediaToUi(media.ChunkEndSeconds(media.Runs[i - 1].Last)), _session.MediaToUi(media.ChunkStartSeconds(media.Runs[i].First))));
        Timeline.Configure(_session.UiDuration, _session.MediaStartSeconds, gaps, _trick, _lane);
        UpdateLoopUi();
    }

    /// <summary>The live session grew or Steam trimmed it: new index for the same recording.</summary>
    private void OnLibraryMediaChanged(object? sender, MediaChangedArgs args) => _ui.TryEnqueue(async () =>
    {
        if (_cleaned || _host is null || _session is null || args.RelativeFolder != _key) return;
        if (args.Media is null || args.Media.ChunkCount == 0)
        {
            AppServices.Toast("Steam removed this recording", null, InfoBarSeverity.Warning, 6);
            _ = _host.StopAsync();
            ShowStatus("This recording was removed", "Steam trimmed it away.", icon: true);
            return;
        }
        var lib = AppServices.Library;
        _media = args.Media;
        UpdateExportUi();
        Task follow = Task.CompletedTask;
        try { follow = _host.Follower!.OnMediaChangedAsync(args.Media); }     // ReplaceMedia runs synchronously; a reload may follow
        catch (ObjectDisposedException) { return; }

        if (lib is not null && (_trick is null || Stopwatch.GetElapsedTime(_trickQueriedAt).TotalSeconds > 15))
        {
            _trick = lib.TrickplayFor(args.RelativeFolder);
            _trickQueriedAt = Stopwatch.GetTimestamp();
        }
        RefreshMarkers(resetFilter: false);
        ConfigureTimeline();
        UpdateLiveUi(_session.Position);
        UpdateTime(_session.UiPosition);
        try { await follow; }
        catch (Exception ex) when (ex is MpvException or TimeoutException or InvalidOperationException or OperationCanceledException) { /* ReloadFailed reports it */ }
    });

    /// <summary>
    /// Markers or trickplay of a recording finished loading (on the UI thread). <see cref="ILibrarySource.FullMarkersFor"/> is lazy: the first
    /// call returns nothing and loads in the background, so without this a cache session never showed its markers.
    /// </summary>
    private void OnLibraryDerivedChanged(object? sender, string relativeFolder)
    {
        OnPlaylistDerivedChanged(sender, relativeFolder);
        if (_cleaned || _media is null || relativeFolder != _key || AppServices.Library is not { } lib) return;
        _trick =lib.TrickplayFor(relativeFolder);
        _trickQueriedAt = Stopwatch.GetTimestamp();
        RefreshMarkers(resetFilter: false);
        ConfigureTimeline();
    }

    /// <summary>
    /// A recording's first sheet can appear after the playlist was built (a live session gets its tiles while Steam writes it):
    /// a row still showing the glyph tries again for that recording only.
    /// </summary>
    private void OnPlaylistDerivedChanged(object? sender, string relativeFolder)
    {
        if (_cleaned) return;
        var row = _playlist.FirstOrDefault(r => r.Key == relativeFolder);
        if (row is not null && row.Thumb is null) _ = LoadPlaylistThumbsAsync([row], _playlistVersion);
    }

    private void OnTrimmed(CacheTrim t) => _ui.TryEnqueue(() =>
    {
        if (_cleaned) return;
        var text = t.Emptied ? "Steam removed everything" : string.Create(CultureInfo.InvariantCulture, $"Steam removed {t.RemovedSeconds:0} s");
        AppServices.Toast(text, t.Emptied ? "The recording is gone from disk." : "Steam trimmed the start of this cache recording; playback jumped to the new start.",
            InfoBarSeverity.Warning, 6);
    });

    // =====================================================================================
    //  playlist (F5, F7)
    // =====================================================================================

    private static string RowTitle(RecordingEntry r) => r.Title ?? (r.Kind == RecordingKind.CacheSession ? "Cache session" : "Clip");

    /// <summary>All recordings of the current game, newest first, the current one selected (highlighted) and scrolled into view.</summary>
    private void BuildPlaylist()
    {
        var index = AppServices.Library?.Current;
        var current = _entry;
        if (index is null || current is null)
        {
            _playlist.Clear();
            _playlistGame = null;
            GameText.Text = _key ?? "";
            PlaylistEmpty.Visibility = Visibility.Visible;
            return;
        }
        var game = current.GameId is null ? "Downloaded clips" : index.Games.FirstOrDefault(g => g.GameId == current.GameId)?.Name ?? "Unknown game";
        GameText.Text = $"{game}  ·  {RowTitle(current)}";

        // Same game and the row is there: only move the highlight (no rebuild, no re-animation).
        if (_playlistGame != (current.GameId ?? "") || _playlist.All(r => r.Key != current.RelativeFolder))
        {
            _playlistGame = current.GameId ?? "";
            _playlist.Clear();
            var rows = index.Recordings.Where(r => r.GameId == current.GameId).OrderByDescending(r => r.StartUtc).Take(120).ToList();
            if (!rows.Contains(current)) rows.Insert(0, current);
            foreach (var r in rows)
            {
                _playlist.Add(new PlaylistRow
                {
                    Key = r.RelativeFolder,
                    Title = RowTitle(r),
                    Meta = $"{Fmt.DateTime(r.StartUtc)}  ·  {Fmt.Timecode(Fmt.Duration(r))}" + (r.MarkerCount > 0 ? $"  ·  {Fmt.Plural(r.MarkerCount, "event")}" : ""),
                    Glyph = r.Kind switch { RecordingKind.CacheSession => "\U0001F4FC", RecordingKind.DownloadedClip => "\U0001F4E5", _ => "\U0001F3AC" },
                    Live = r.Live,
                });
            }
            _ = LoadPlaylistThumbsAsync([.. _playlist], ++_playlistVersion);
        }
        PlaylistEmpty.Visibility = _playlist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = _playlist.FirstOrDefault(r => r.Key == current.RelativeFolder);
        PlaylistList.SelectedItem = selected;
        if (selected is not null) PlaylistList.ScrollIntoView(selected, ScrollIntoViewAlignment.Leading);
    }

    /// <summary>F7: Steam's own <c>thumbnail.jpg</c> of a clip, else the first trickplay tile. One at a time; a newer playlist stops it.</summary>
    private async Task LoadPlaylistThumbsAsync(List<PlaylistRow> rows, int version)
    {
        var lib = AppServices.Library;
        if (lib is null) return;
        foreach (var row in rows)
        {
            if (version != _playlistVersion || _cleaned) return;
            try
            {
                var media = lib.GetMedia(row.Key);
                var jpg = await Task.Run(() => PlaylistThumbs.ReadSteamThumbnail(row.Key, media?.Folder));
                if (version != _playlistVersion || _cleaned) return;
                if (jpg is not null && await PlaylistThumbs.DecodeAsync(jpg, 156) is { } image)
                {
                    row.ThumbKeepAlive = image.KeepAlive;
                    row.Thumb = image.Source;
                    continue;
                }
                var trick = await Task.Run(() => lib.TrickplayFor(row.Key));
                if (version != _playlistVersion || _cleaned) return;
                if (trick is { Count: > 0 } && trick.FindNearest(media?.TfdtOriginSeconds ?? 0) is { } tile && await _thumbTiles.GetAsync(tile) is { } src)
                    row.Thumb = src;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                /* no thumbnail: the glyph placeholder stays */
            }
        }
    }

    private void Playlist_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistRow row && row.Key != _key) _ = LoadAsync(row.Key, null);
        Focus(FocusState.Programmatic);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => AppServices.Window?.NavigateToLibrary();

    // =====================================================================================
    //  mpv / session events (mpv's thread: marshal, never block)
    // =====================================================================================

    private void OnSessionPosition(double media)
    {
        Volatile.Write(ref _latestTimePos, media);
        if (Interlocked.Exchange(ref _posQueued, 1) == 0) _ui.TryEnqueue(ApplyPosition);
    }

    private void OnSessionReloaded(SessionReload r) => _ui.TryEnqueue(() =>
    {
        if (_cleaned) return;
        Volatile.Write(ref _latestTimePos, r.StartSeconds);
        ApplyPosition();
        UpdatePlayGlyph();
    });

    private void OnSessionEnd(double media) => _ui.TryEnqueue(() =>
    {
        if (_cleaned) return;
        UpdatePlayGlyph();
        if (_session?.Media is { Live: true }) UpdateLiveUi(media);
    });

    private void OnSessionReloadFailed(Exception ex) => _ui.TryEnqueue(() =>
    {
        if (_cleaned) return;
        AppServices.Toast("Couldn't load that part", ex.Message, InfoBarSeverity.Error, 8);
    });

    private void OnMpvPause(bool paused) => _ui.TryEnqueue(() => { if (!_cleaned) UpdatePlayGlyph(); });
    private void OnMpvSpeed(double speed) => _ui.TryEnqueue(() => { if (!_cleaned) SyncSpeedUi(speed); });
    private void OnMpvVolume(double volume) => _ui.TryEnqueue(() => { if (!_cleaned) SyncVolumeUi(); });
    private void OnMpvMute(bool muted) => _ui.TryEnqueue(() => { if (!_cleaned) SyncVolumeUi(); });
    private void OnMeasured(MetricSample s) => _ui.TryEnqueue(() => { if (!_cleaned && _debug) UpdateDebug(); });

    private void OnDeviceLost() => _ui.TryEnqueue(() =>
        AppServices.Toast("The graphics device was reset", "Reopen the recording (or restart the app) to get the picture back.", InfoBarSeverity.Error, 10));

    /// <summary>One UI item per burst of mpv position events.</summary>
    private void ApplyPosition()
    {
        Interlocked.Exchange(ref _posQueued, 0);
        if (_cleaned || _session is not { Media: not null } session || Timeline.IsScrubbing) return;
        var media = Volatile.Read(ref _latestTimePos);
        if (double.IsNaN(media)) return;

        var ui = session.MediaToUi(media);
        Timeline.Position = ui;
        Timeline.LoopEngaged = session.LoopEngaged;
        UpdateTime(ui);
        UpdateLiveUi(media);

        if (_key is not null && _host is not null)
        {
            _host.Positions.Set(_key, media, session.UiDuration);
            if (_flushClock.Elapsed.TotalSeconds >= 10) { _flushClock.Restart(); _host.Positions.Flush(); }
        }
        if (_debug && _debugClock.ElapsedMilliseconds >= 300) UpdateDebug();
    }

    private void SaveResume(bool flush)
    {
        if (_key is null || _host is null || _session?.Media is null) return;
        var pos = _session.Position;
        if (double.IsFinite(pos)) _host.Positions.Set(_key, pos, _session.UiDuration);
        if (flush) _host.Positions.Flush();
    }

    // =====================================================================================
    //  UI state
    // =====================================================================================

    /// <summary>F2/F3: <c>HH:MM:SS.FF / HH:MM:SS.FF</c> (position: FF = 1-based frame in the second; duration: whole frames).</summary>
    private void UpdateTime(double ui) =>
        TimeText.Text = FrameTimecode.PositionOfLength(ui, _session?.UiDuration ?? 0, _fps);

    /// <summary>
    /// The stream's frame rate: measured from its chunks. mpv's <c>container-fps</c> (else <c>estimated-vf-fps</c>) is only the fallback when no
    /// chunk is readable: it flips between 60 and 120 across reloads of a 60 fps session, which doubled FF and halved the frame-back step.
    /// </summary>
    private double ReadFps()
    {
        try
        {
            if (_chunkFps is { } known) _fps = known;
            else if (_host?.Mpv is { } mpv)
            {
                var fps = mpv.GetDouble("container-fps");
                if (!(fps is > 1 and < 1000)) fps = mpv.GetDouble("estimated-vf-fps");
                if (fps is > 1 and < 1000) _fps = fps;
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or MpvException) { /* keep the last known rate */ }
        Timeline.Fps = _fps;
        return _fps;
    }

    private void UpdatePlayGlyph()
    {
        if (_session is null) return;
        var paused = _session.UserPaused || _session.AtEnd;
        PlayGlyph.Glyph = paused ? "" : "";
    }

    private void UpdateLiveUi(double media)
    {
        if (_session?.Media is not { Live: true } m)
        {
            LivePill.Visibility = Visibility.Collapsed;
            return;
        }
        LivePill.Visibility = Visibility.Visible;
        var behind = Math.Max(0, _session.MediaEndSeconds - media);
        var atEdge = behind <= LiveEdgeSeconds;
        LiveDot.Fill = atEdge ? (Brush)Application.Current.Resources["LiveBrush"] : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175));
        LiveText.Text = atEdge ? "LIVE" : $"live  ·  {MarkerIcons.Timecode(behind, false)} behind  ·  go live";
    }

    private void LivePill_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        GoLive();
    }

    private void GoLive()
    {
        if (_session?.Media is not { Live: true }) return;
        RunSeek(() => _host!.SeekAsync(_session.MediaEndSeconds - 3));
    }

    private void UpdateLoopUi()
    {
        var a = _ab.A is { } x && _session is not null ? _session.MediaToUi(x) : (double?)null;
        var b = _ab.B is { } y && _session is not null ? _session.MediaToUi(y) : (double?)null;
        Timeline.SetLoop(a, b);
        LoopButton.IsChecked = _ab.A is not null;
        LoopText.Text = a is null ? "" : b is null
            ? $"A {FrameTimecode.Position(a.Value, _fps)}  ·  set B"
            : $"A {FrameTimecode.Position(a.Value, _fps)}  ·  B {FrameTimecode.Position(b.Value, _fps)}";
        UpdateExportUi();
    }

    /// <summary>
    /// Hands the loop (media time) to the session, which reloads at A when the position reaches B (mpv's own ab-loop seeks inside the EDL
    /// and comes back short, P1). Only a complete loop is set.
    /// </summary>
    private void ApplyAbLoop()
    {
        if (_session is null) return;
        try
        {
            if (_ab.Active) _session.SetLoop(_ab.A, _ab.B);
            else _session.SetLoop(null, null);
        }
        catch (Exception e) when (e is MpvException or ObjectDisposedException) { PlayerLog.Write("ab-loop: " + e.Message); }
    }

    private void SyncSpeedUi(double speed)
    {
        var i = Array.FindIndex(Speeds, s => Math.Abs(s - speed) < 0.001);
        if (i >= 0) _speedIndex = i;
        SpeedText.Text = Speeds[_speedIndex].ToString("0.##", CultureInfo.InvariantCulture) + "×";
        foreach (var item in SpeedMenu.Items.OfType<ToggleMenuFlyoutItem>())
            item.IsChecked = item.Tag is double d && Math.Abs(d - speed) < 0.001;
    }

    private void SyncVolumeUi()
    {
        if (_host?.Mpv is not { } mpv) return;
        _syncingVolume = true;
        VolumeSlider.Value = Math.Min(100, mpv.Volume);
        MuteButton.IsChecked = mpv.Muted;
        MuteGlyph.Glyph = mpv.Muted || mpv.Volume <= 0 ? "" : "";
        _syncingVolume = false;
    }

    private void Pulse(string glyph)
    {
        PlayPulseIcon.Glyph = glyph;
        // Implicit opacity animation (ScalarTransition on the element): show at once, fade out a moment later.
        PlayPulse.OpacityTransition = null;
        PlayPulse.Opacity = 0.9;
        PlayPulse.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(450) };
        _pulseTimer ??= CreatePulseTimer();
        _pulseTimer.Stop();
        _pulseTimer.Start();
    }

    private DispatcherQueueTimer CreatePulseTimer()
    {
        var t = _ui.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(120);
        t.IsRepeating = false;
        t.Tick += (_, _) => PlayPulse.Opacity = 0;
        return t;
    }

    // =====================================================================================
    //  actions
    // =====================================================================================

    /// <summary>Runs a seek-like task, reporting a failure as a toast. <paramref name="then"/> runs when it ended either way.</summary>
    private async void RunSeek(Func<Task> action, Action? then = null)
    {
        try { await action(); }
        catch (OperationCanceledException) { /* superseded or closing */ }
        catch (Exception ex) when (ex is MpvException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            if (!_cleaned) AppServices.Toast("Couldn't seek", ex.Message, InfoBarSeverity.Warning, 5);
        }
        finally { then?.Invoke(); }
    }

    private void Timeline_SeekRequested(double ui)
    {
        if (_session is null || _host is null || _session.Media is null) return;
        var media = _session.UiToMedia(ui);
        RunSeek(() => _host.SeekAsync(media));
    }

    private void JumpTo(LaneMarker m)
    {
        if (_host is null) return;
        RunSeek(() => _host.SeekAsync(MarkerNavigation.LandingSeconds(m.Source)));
        Focus(FocusState.Programmatic);
    }

    private void Event_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EventRow row) JumpTo(row.Marker);
    }

    private IReadOnlyList<Marker> NavMarkers => [.. _lane.Where(m => MarkerKinds.ShowOnLane(m.Kind) && Shown(m)).Select(m => m.Source)];

    private void NextEvent()
    {
        if (_session is null || _host is null) return;
        var m = MarkerNavigation.Next(NavMarkers, _session.Position);
        if (m is null) { AppServices.Toast("No later event", null, InfoBarSeverity.Informational, 2); return; }
        RunSeek(() => _host.SeekAsync(MarkerNavigation.LandingSeconds(m)));
    }

    private void PreviousEvent()
    {
        if (_session is null || _host is null) return;
        var m = MarkerNavigation.Previous(NavMarkers, _session.Position);
        if (m is null) { AppServices.Toast("No earlier event", null, InfoBarSeverity.Informational, 2); return; }
        RunSeek(() => _host.SeekAsync(MarkerNavigation.LandingSeconds(m)));
    }

    private void TogglePlay()
    {
        if (_session?.Media is null || _host is null) return;
        if (_session.AtEnd)
        {
            _session.SetPause(false);
            RunSeek(() => _host.SeekAsync(_session.MediaStartSeconds));
            Pulse("");
            UpdatePlayGlyph();
            return;
        }
        _session.TogglePause();
        var paused = _session.UserPaused;
        Pulse(paused ? "" : "");
        UpdatePlayGlyph();
        if (paused) SaveResume(flush: true);
    }

    private void SeekBy(double seconds)
    {
        if (_host is null || _session?.Media is null) return;
        RunSeek(() => _host.SeekRelativeAsync(seconds));
    }

    private double FrameSeconds() => 1 / ReadFps();

    private void FrameForward()
    {
        if (_session?.Media is null) return;
        try { _session.FrameStep(); }
        catch (MpvException ex) { AppServices.Toast("Couldn't step", ex.Message, InfoBarSeverity.Warning, 3); }
        UpdatePlayGlyph();
    }

    private void FrameBackward()
    {
        if (_session?.Media is null || _host is null) return;
        _session.SetPause(true);
        UpdatePlayGlyph();
        RunSeek(() => _host.StepBackAsync(FrameSeconds()));       // a reload: mpv cannot step back across a chunk
    }

    private void SetSpeedIndex(int i)
    {
        _speedIndex = Math.Clamp(i, 0, Speeds.Length - 1);
        _session?.SetSpeed(Speeds[_speedIndex]);
        SyncSpeedUi(Speeds[_speedIndex]);
    }

    private void BuildSpeedMenu()
    {
        foreach (var s in Speeds)
        {
            var item = new ToggleMenuFlyoutItem { Text = s.ToString("0.##", CultureInfo.InvariantCulture) + "×", Tag = s, IsChecked = s == 1 };
            item.Click += (_, _) => SetSpeedIndex(Array.IndexOf(Speeds, s));
            SpeedMenu.Items.Add(item);
        }
    }

    private void ToggleMute()
    {
        if (_host?.Mpv is not { } mpv || _session is null) return;
        _session.SetMute(!mpv.Muted);
    }

    private void SetAbPoint(char which)
    {
        if (_session?.Media is null) return;
        var pos = _session.Position;
        if (which == 'A') _ab.SetA(pos);
        else if (!_ab.SetB(pos)) AppServices.Toast("B must come after A", "Press A first, then B later in the recording.", InfoBarSeverity.Informational, 3);
        ApplyAbLoop();
        UpdateLoopUi();
    }

    // ---- fullscreen -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Asks for fullscreen (or back). Deferred out of the current input event: changing the presenter and the layout from inside a pointer
    /// handler of an element that the change re-lays out made WinUI fail fast (seen with double-tap leaving fullscreen).
    /// </summary>
    private void SetFullscreen(bool fs) => _ui.TryEnqueue(DispatcherQueuePriority.Low, () => { if (!_cleaned) SetFullscreenNow(fs); });

    private void SetFullscreenNow(bool fs)
    {
        try { _appWindow?.SetPresenter(fs ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { return; }      // the window is closing
        ApplyFullscreenState(fs);
        FitExportLayout();
        // A presenter change can leave the keyboard focus nowhere; Windows moves it around for a moment after the change, so claim it again
        // once things have settled (a one-shot timer, not polling).
        Focus(FocusState.Programmatic);
        _focusTimer ??= CreateFocusTimer();
        _focusTimer.Stop();
        _focusTimer.Start();
    }

    /// <summary>
    /// After a presenter change or Alt-Tab the XAML keyboard focus can be nowhere, and then no key reaches the page: put it back on the
    /// page (unless something inside the page, such as a list item, already has it).
    /// </summary>
    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        _ui.TryEnqueue(DispatcherQueuePriority.Low, RestoreFocus);
    }

    private DispatcherQueueTimer CreateFocusTimer()
    {
        var t = _ui.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(300);
        t.IsRepeating = false;
        t.Tick += (_, _) => { if (!_cleaned) { _window?.Activate(); Focus(FocusState.Programmatic); } };
        return t;
    }

    private void RestoreFocus()
    {
        if (_cleaned || XamlRoot is null) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is null) Focus(FocusState.Programmatic);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange) return;
        _ui.TryEnqueue(() =>
        {
            if (_cleaned) return;
            ApplyFullscreenState(sender.Presenter.Kind == AppWindowPresenterKind.FullScreen);
            _ui.TryEnqueue(DispatcherQueuePriority.Low, RestoreFocus);
        });
    }

    private void ApplyFullscreenState(bool fs)
    {
        if (_fullscreen == fs) return;
        _fullscreen = fs;
        AppServices.Window?.SetChromeVisible(!fs);
        ApplyLayout(fs);
        FullscreenGlyph.Glyph = fs ? "" : "";
        if (fs) { ShowChrome(); RestartIdle(); }
        else ShowChrome();
    }

    /// <summary>Below this page width "Up next" gives way (310 | * | 392 leaves the video too narrow for its transport).</summary>
    private const double CompactBelow = 1260;

    private void PageRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = PageRoot.ActualWidth < CompactBelow;
        if (compact == _compact) return;
        _compact = compact;
        if (!_fullscreen) ApplyLayout(false);
    }

    /// <summary>
    /// Transport widths: every control stays visible on one row. Below <see cref="SliderInlineFrom"/> the volume slider moves into the
    /// volume button's flyout; below <see cref="LabelsFrom"/> the A–B and speed labels shrink to their icons / value.
    /// </summary>
    private const double SliderInlineFrom = 790, LabelsFrom = 700;
    private int _transportTier = -1;   // 0 = full, 1 = slider in flyout, 2 = slider in flyout + short labels

    private void Transport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        var tier = w >= SliderInlineFrom ? 0 : w >= LabelsFrom ? 1 : 2;
        if (tier == _transportTier) return;
        // Not inside the layout pass that reported the size: re-parenting the slider there is a layout re-entrancy.
        DispatcherQueue.TryEnqueue(() => ApplyTransportTier(tier));
    }

    private void ApplyTransportTier(int tier)
    {
        if (tier == _transportTier) return;
        _transportTier = tier;
        var inFlyout = tier > 0;
        if (inFlyout)
        {
            VolumeFlyoutButton.Visibility = Visibility.Visible;
            if (TransportRight.Children.Remove(VolumeSlider))
            {
                VolumeSlider.Width = 140;
                VolumeFlyoutButton.Flyout = new Flyout
                {
                    Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight,
                    Content = new Grid { Padding = new Thickness(4, 2, 4, 2), Children = { VolumeSlider } },
                };
            }
        }
        else
        {
            VolumeFlyoutButton.Visibility = Visibility.Collapsed;
            if (VolumeFlyoutButton.Flyout is Flyout { Content: Grid host } f)
            {
                f.Hide();
                host.Children.Remove(VolumeSlider);
                f.Content = null;
                VolumeFlyoutButton.Flyout = null;
                VolumeSlider.Width = 92;
                TransportRight.Children.Insert(TransportRight.Children.IndexOf(FullscreenButton), VolumeSlider);
            }
        }
        LoopLabel.Visibility = tier >= 2 ? Visibility.Collapsed : Visibility.Visible;
        SpeedIcon.Visibility = tier >= 2 ? Visibility.Collapsed : Visibility.Visible;
        // the tightest tier also trims gaps and padding (the video column is ~620 px at the minimum window)
        TransportRight.Spacing = tier >= 2 ? 2 : 4;
        TimeStack.Margin = new Thickness(tier >= 2 ? 6 : 10, 0, 0, 0);
        var pad = tier >= 2 ? new Thickness(6, 6, 6, 6) : new Thickness(8, 6, 8, 6);
        LoopButton.Padding = pad;
        MuteButton.Padding = pad;
    }

    private void ApplyLayout(bool fs)
    {
        var hideLeft = fs || _compact;
        LeftCard.Visibility = hideLeft ? Visibility.Collapsed : Visibility.Visible;
        RightPanel.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
        LeftCol.Width = new GridLength(hideLeft ? 0 : 310);
        RightCol.Width = new GridLength(fs ? 0 : 392);
        PageRoot.Padding = fs ? new Thickness(0) : new Thickness(12, 4, 12, 12);
        PageRoot.ColumnSpacing = fs ? 0 : 12;       // (a hidden column has width 0 but its spacing stays: acceptable)
        CenterGrid.RowSpacing = fs ? 0 : 10;
        VideoHost.CornerRadius = new CornerRadius(fs ? 0 : 8);
        Grid.SetRowSpan(VideoHost, fs ? 2 : 1);
        if (fs)
        {
            // Explicit values both ways: ClearValue on a style-set ThemeResource property crashed WinUI on the second round trip.
            // The card's own background stays: the video (padding 0, spanning both rows) covers it.
            CenterCard.Padding = new Thickness(0);
            CenterCard.BorderThickness = new Thickness(0);
            CenterCard.CornerRadius = new CornerRadius(0);
            Lower.VerticalAlignment = VerticalAlignment.Bottom;
            Lower.Padding = new Thickness(24, 56, 24, 18);
            var scrim = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
            scrim.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 0 });
            scrim.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xCC, 0, 0, 0), Offset = 0.55 });
            scrim.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xE6, 0, 0, 0), Offset = 1 });
            Lower.Background = scrim;
        }
        else
        {
            CenterCard.Padding = new Thickness(12);
            CenterCard.BorderThickness = new Thickness(1);      // the Card style's values
            CenterCard.CornerRadius = new CornerRadius(10);
            Lower.VerticalAlignment = VerticalAlignment.Stretch;
            Lower.Padding = new Thickness(0);
            Lower.Background = null;
        }
    }

    private void ShowChrome()
    {
        Lower.Opacity = 1;
        Lower.IsHitTestVisible = true;
    }

    private void HideChrome()
    {
        Lower.Opacity = 0;
        Lower.IsHitTestVisible = false;
    }

    private void RestartIdle()
    {
        if (!_fullscreen || _idleTimer is null) return;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void PageRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        ShowChrome();
        RestartIdle();
    }

    // =====================================================================================
    //  buttons and keys
    // =====================================================================================

    private void PlayPause_Click(object sender, RoutedEventArgs e) { TogglePlay(); Focus(FocusState.Programmatic); }
    private void Back5_Click(object sender, RoutedEventArgs e) { SeekBy(-5); Focus(FocusState.Programmatic); }
    private void Fwd5_Click(object sender, RoutedEventArgs e) { SeekBy(5); Focus(FocusState.Programmatic); }
    private void FrameBack_Click(object sender, RoutedEventArgs e) { FrameBackward(); Focus(FocusState.Programmatic); }
    private void FrameFwd_Click(object sender, RoutedEventArgs e) { FrameForward(); Focus(FocusState.Programmatic); }
    private void Mute_Click(object sender, RoutedEventArgs e) { ToggleMute(); Focus(FocusState.Programmatic); }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) { SetFullscreen(!_fullscreen); Focus(FocusState.Programmatic); }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.Media is null) { LoopButton.IsChecked = false; return; }
        var result = _ab.Cycle(_session.Position);
        if (result == "B must be after A") AppServices.Toast(result, null, InfoBarSeverity.Informational, 3);
        ApplyAbLoop();
        UpdateLoopUi();
        Focus(FocusState.Programmatic);
    }

    private void Volume_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingVolume) return;
        _session?.SetVolume(e.NewValue);
        if (e.NewValue > 0 && _host?.Mpv is { Muted: true }) _session?.SetMute(false);
    }

    private void Video_Tapped(object sender, TappedRoutedEventArgs e)
    {
        TogglePlay();
        Focus(FocusState.Programmatic);
    }

    private void Video_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => SetFullscreen(!_fullscreen);    // the two taps toggled play twice: net zero

    private void VideoPanel_SizeChanged(object sender, SizeChangedEventArgs e) => SyncSize();

    private void VideoPanel_ScaleChanged(SwapChainPanel sender, object args) => SyncSize();

    private void SyncSize()
    {
        if (!_attached || _host is null) return;
        var scale = (float)(VideoPanel.XamlRoot?.RasterizationScale ?? 1.0);
        var w = (int)Math.Round(VideoPanel.ActualWidth * scale);
        var h = (int)Math.Round(VideoPanel.ActualHeight * scale);
        if (w > 0 && h > 0) _host.Resize(w, h, scale);
    }

    private static bool Down(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var step = shift ? 30 : 5;
        var handled = true;
        switch ((int)e.Key)
        {
            case (int)VirtualKey.D when ctrl && shift: ToggleDebug(); break;
            case (int)VirtualKey.Space:
            case (int)VirtualKey.K: TogglePlay(); break;
            case (int)VirtualKey.J:
            case (int)VirtualKey.Left: SeekBy(-step); break;
            case (int)VirtualKey.L:
            case (int)VirtualKey.Right: SeekBy(step); break;
            case 188: FrameBackward(); break;                                       // ,
            case 190: FrameForward(); break;                                        // .
            case 219: SetSpeedIndex(_speedIndex - 1); break;                        // [
            case 221: SetSpeedIndex(_speedIndex + 1); break;                        // ]
            case (int)VirtualKey.M: ToggleMute(); break;
            case (int)VirtualKey.F: SetFullscreen(!_fullscreen); break;
            case (int)VirtualKey.Escape:
                if (_fullscreen) SetFullscreen(false);
                else if (_ab.A is not null) { _ab.Clear(); ApplyAbLoop(); UpdateLoopUi(); }
                else handled = false;
                break;
            case (int)VirtualKey.A: SetAbPoint('A'); break;
            case (int)VirtualKey.B: SetAbPoint('B'); break;
            case (int)VirtualKey.S: _ = TakeScreenshotAsync(); break;
            case (int)VirtualKey.E when Down(VirtualKey.Control): _ = EnqueueAsync(_exportChoice); break;
            case (int)VirtualKey.N: NextEvent(); break;
            case (int)VirtualKey.P: PreviousEvent(); break;
            case (int)VirtualKey.Home: if (_session?.Media is not null) RunSeek(() => _host!.SeekAsync(_session.MediaStartSeconds)); break;
            case (int)VirtualKey.End:
                if (_session?.Media is { Live: true }) GoLive();
                else if (_session?.Media is not null) RunSeek(() => _host!.SeekAsync(_session.MediaEndSeconds));
                break;
            default: handled = false; break;
        }
        if (!handled) return;
        e.Handled = true;
        if (_fullscreen) { ShowChrome(); RestartIdle(); }
    }

    // =====================================================================================
    //  debug overlay (Ctrl+Shift+D)
    // =====================================================================================

    private void ToggleDebug()
    {
        _debug = !_debug;
        DebugCard.Visibility = _debug ? Visibility.Visible : Visibility.Collapsed;
        if (_debug) UpdateDebug();
    }

    private void UpdateDebug()
    {
        if (_host?.Mpv is not { } mpv || _host.Renderer is not { } renderer) return;
        _debugClock.Restart();
        var m = _host.Metrics;
        var sb = new StringBuilder();
        void Line(string s) => sb.AppendLine(s);
        string Sample(MetricKind k, double budget)
        {
            var last = m.Last(k);
            var s = m.Summary(k);
            return last is null
                ? "-"
                : string.Create(CultureInfo.InvariantCulture, $"{last.Milliseconds,6:0.0} ms  (n={s.Count} p50 {s.P50:0} p95 {s.P95:0} max {s.Max:0}; budget {budget:0})");
        }
        Line("open -> first frame " + Sample(MetricKind.Open, 250));
        Line("seek -> first frame " + Sample(MetricKind.Seek, 150));
        try
        {
            var stats = renderer.Stats();
            var (pw, ph) = renderer.PixelSize;
            Line(string.Create(CultureInfo.InvariantCulture,
                $"decode {_host.DecodeMode} (hwdec-current={mpv.GetString("hwdec-current") ?? "-"})  adapter {renderer.AdapterName}"));
            Line(string.Create(CultureInfo.InvariantCulture,
                $"render {pw}x{ph}  present p50 {stats.P50Ms:0.0} p99 {stats.P99Ms:0.0} max {stats.MaxMs:0.0} ms  errors {stats.PresentErrors}  lost {stats.DeviceRemoved}"));
            Line(string.Create(CultureInfo.InvariantCulture,
                $"mem  RSS {Environment.WorkingSet / 1048576.0:0} MB  GPU {renderer.GpuLocalMb():0} MB   pre-warm {(_host.PreWarmMilliseconds is { } p ? p.ToString("0", CultureInfo.InvariantCulture) + " ms" : "-")}"));
        }
        catch (ObjectDisposedException) { return; }
        if (_session is { } s)
        {
            var w = s.Window;
            Line(string.Create(CultureInfo.InvariantCulture,
                $"time {s.Position:0.00}s  ui {s.UiPosition:0.00}/{s.UiDuration:0.0}s  speed {mpv.Speed:0.##}x  window {(w.Count > 0 ? w[0] + ".." + w[^1] : "-")}  reloading {s.IsReloading}"));
        }
        DebugText.Text = sb.ToString().TrimEnd();
    }
}

