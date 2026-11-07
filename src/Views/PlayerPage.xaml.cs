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
/// The Player page (plan §4.2): Up next | video, timeline, transport | Events and Export. Navigation parameter: <see cref="PlayerRequest"/>
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
    private readonly ObservableCollection<UpNextRow> _upNext = [];
    private readonly ObservableCollection<UIElement> _chips = [];
    private readonly HashSet<MarkerKind> _visibleKinds = [];
    private readonly AbLoop _ab = new();
    private readonly Stopwatch _flushClock = Stopwatch.StartNew();
    private readonly Stopwatch _debugClock = Stopwatch.StartNew();

    private PlayerHost? _host;
    private PlaybackSession? _session;
    private DispatcherQueueTimer? _idleTimer, _pulseTimer, _focusTimer;
    private bool _compact, _transportNarrow;
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
    private bool _started, _attached, _cleaned, _subscribed, _fullscreen, _trickPaused, _overLower, _loopJumping, _syncingVolume, _debug;

    public PlayerRequest? Request { get; private set; }

    public PlayerPage()
    {
        InitializeComponent();
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        EventsList.ItemsSource = _eventRows;
        UpNextList.ItemsSource = _upNext;
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
        if (AppServices.Library is { } lib) lib.MediaChanged += OnLibraryMediaChanged;
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
        if (AppServices.Library is { } lib) lib.MediaChanged -= OnLibraryMediaChanged;
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
        UpdateLoopUi();
        _entry = lib?.Current?.Recordings.FirstOrDefault(r => r.RelativeFolder == key);
        BuildUpNext();
        ClearEvents();
        ShowStatus("Opening…", null, busy: true);

        var media = lib?.GetMedia(key);
        if (media is null || media.ChunkCount == 0)
        {
            ShowStatus("This recording isn't available", "Steam may have removed it, or its files are not readable.", icon: true);
            Timeline.Configure(0, 0, [], null, []);
            TimeText.Text = "0:00.0 / 0:00";
            return;
        }

        var first = media.ChunkStartSeconds(media.FirstChunk!.Value);
        var last = media.LastChunk!.Value * media.SegmentSeconds;
        double? startMedia = null;
        var resumed = false;
        if (startUi is { } s) startMedia = first + s;
        else if (ResumePositions.ResumeMediaSeconds(host.Positions.Get(key), first, last) is { } r) { startMedia = r; resumed = true; }
        else if (media.Live) startMedia = Math.Max(first, last - LiveStartBehindSeconds);

        _media = media;
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
        UpdatePlayGlyph();
        ApplyPosition();
        if (resumed) AppServices.Toast("Resumed", $"Continuing at {MarkerIcons.Timecode(session.UiPosition, false)}.", InfoBarSeverity.Informational, 3);
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
            gaps.Add((_session.MediaToUi(media.Runs[i - 1].Last * media.SegmentSeconds), _session.MediaToUi(media.ChunkStartSeconds(media.Runs[i].First))));
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

    private void OnTrimmed(CacheTrim t) => _ui.TryEnqueue(() =>
    {
        if (_cleaned) return;
        var text = t.Emptied ? "Steam removed everything" : string.Create(CultureInfo.InvariantCulture, $"Steam removed {t.RemovedSeconds:0} s");
        AppServices.Toast(text, t.Emptied ? "The recording is gone from disk." : "Steam trimmed the start of this cache recording; playback jumped to the new start.",
            InfoBarSeverity.Warning, 6);
    });

    // =====================================================================================
    //  up next
    // =====================================================================================

    private void BuildUpNext()
    {
        _upNext.Clear();
        var index = AppServices.Library?.Current;
        var current = _entry;
        if (index is null || current is null)
        {
            GameText.Text = _key ?? "";
            UpNextEmpty.Visibility = Visibility.Visible;
            return;
        }
        var game = current.GameId is null ? "Downloaded clips" : index.Games.FirstOrDefault(g => g.GameId == current.GameId)?.Name ?? "Unknown game";
        var title = current.Title ?? (current.Kind == RecordingKind.CacheSession ? "Cache session" : "Clip");
        GameText.Text = $"{game}  ·  {title}";
        foreach (var r in index.Recordings.Where(r => r.GameId == current.GameId && r.RelativeFolder != current.RelativeFolder)
                     .OrderByDescending(r => r.StartUtc).Take(80))
        {
            _upNext.Add(new UpNextRow
            {
                Key = r.RelativeFolder,
                Title = r.Title ?? (r.Kind == RecordingKind.CacheSession ? "Cache session" : "Clip"),
                Meta = $"{Fmt.DateTime(r.StartUtc)}  ·  {Fmt.Timecode(Fmt.Duration(r))}" + (r.MarkerCount > 0 ? $"  ·  {Fmt.Plural(r.MarkerCount, "event")}" : ""),
                Glyph = r.Kind switch { RecordingKind.CacheSession => "\U0001F4FC", RecordingKind.DownloadedClip => "\U0001F4E5", _ => "\U0001F3AC" },
                Live = r.Live,
            });
        }
        UpNextEmpty.Visibility = _upNext.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpNext_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is UpNextRow row) _ = LoadAsync(row.Key, null);
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
        _loopJumping = false;
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
        UpdateTime(ui);
        UpdateLiveUi(media);

        if (_ab.ShouldJump(media) && !_loopJumping && _ab.A is { } a)
        {
            _loopJumping = true;
            RunSeek(() => _host!.SeekAsync(a), then: () => _loopJumping = false);
        }

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

    private void UpdateTime(double ui) =>
        TimeText.Text = $"{MarkerIcons.Timecode(ui)} / {MarkerIcons.Timecode(_session?.UiDuration ?? 0, false)}";

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
        LoopText.Text = a is null ? "" : b is null ? $"A {MarkerIcons.Timecode(a.Value)}  ·  set B" : $"A {MarkerIcons.Timecode(a.Value)}  ·  B {MarkerIcons.Timecode(b.Value)}";
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

    private double FrameSeconds()
    {
        var fps = double.NaN;
        try
        {
            var mpv = _host?.Mpv;
            if (mpv is not null)
            {
                fps = mpv.GetDouble("container-fps");
                if (!(fps > 0)) fps = mpv.GetDouble("estimated-vf-fps");
            }
        }
        catch (ObjectDisposedException) { }
        return fps > 0 && fps < 1000 ? 1 / fps : 1.0 / 30;
    }

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

    /// <summary>Narrow video column: the right cluster of the transport moves to a second row.</summary>
    private void Transport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 700 && !_fullscreen;
        if (narrow == _transportNarrow) return;
        _transportNarrow = narrow;
        Grid.SetRow(TransportRight, narrow ? 1 : 0);
        Grid.SetColumn(TransportRight, narrow ? 0 : 3);
        Grid.SetColumnSpan(TransportRight, narrow ? 4 : 1);
        TransportRight.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Left;
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
                else if (_ab.A is not null) { _ab.Clear(); UpdateLoopUi(); }
                else handled = false;
                break;
            case (int)VirtualKey.A: SetAbPoint('A'); break;
            case (int)VirtualKey.B: SetAbPoint('B'); break;
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

