// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Globalization;
using FastEncoder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Net;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Services;

namespace SteamPoweredPlayer.Views;

/// <summary>One remote recording on the Downloads page.</summary>
public sealed class DownloadRow(string libraryId, RemoteRecording recording, string title) : INotifyPropertyChanged
{
    public string LibraryId { get; } = libraryId;
    public RemoteRecording Recording { get; } = recording;
    public string Title { get; } = title;
    public DownloadRow Self => this;

    public string Detail { get; private set; } = "";
    public double Fraction { get; private set; }
    public Visibility ProgressVisibility { get; private set; } = Visibility.Collapsed;
    public string ActionText { get; private set; } = "Download";
    public bool CanPlay { get; private set; }
    public bool Pinned { get; private set; }

    /// <summary>Markers read from the library; null until they are known (see <see cref="MarkersFailed"/>).</summary>
    public IReadOnlyList<Marker>? Markers { get; set; }

    /// <summary>The library could not be asked for markers (offline or timed out).</summary>
    public bool MarkersFailed { get; set; }

    public bool CanAround { get; private set; }
    public string AroundTooltip { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="cached">Files of this recording already in the cache (init segments count).</param>
    /// <param name="coverage">Chunks complete on this PC: a part of the recording is shown as a part, not as "Downloaded".</param>
    public void Update(DownloadItem? item, int cached, DownloadCoverage coverage, bool pinned)
    {
        var length = Pipeline.Time(Recording.Media.DurationSeconds);
        var size = Recording.Media.BitsPerSecond is { } bits ? "  ·  ≈ " + Pipeline.Size((long)(bits / 8 * Recording.Media.DurationSeconds)) : "";
        var start = Recording.Name.StartUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var head = $"{start}  ·  {length}{size}";
        Pinned = pinned;
        CanPlay = cached > 2;                                       // both init segments plus at least one chunk
        var state = item?.State;
        var busy = state is DownloadItemState.Waiting or DownloadItemState.Running;
        Fraction = item is not null && busy ? item.Fraction : coverage.Fraction;
        ProgressVisibility = busy || state == DownloadItemState.CannotReach ? Visibility.Visible : Visibility.Collapsed;
        ActionText = busy ? "Cancel" : coverage.IsComplete ? "Downloaded" : coverage.Have > 0 ? "Continue" : "Download";

        var marks = Markers?.Count(m => m.Alignment != MarkerAlignment.None) ?? 0;
        CanAround = !busy && !coverage.IsComplete && marks > 0;
        AroundTooltip = Markers is null ? (MarkersFailed ? "Couldn't read this recording's markers from the library." : "Reading this recording's markers…")
            : marks == 0 ? "This recording has no markers."
            : coverage.IsComplete ? "The whole recording is already on this PC."
            : $"Download only 30 s before and after each of its {marks} marker{(marks == 1 ? "" : "s")}.";

        var data = item is { BytesFetched: > 0 } ? $"  ·  {Pipeline.Size(item.BytesFetched)} downloaded" : "";
        var onPc = $"{Pipeline.Time(coverage.Seconds)} of {length} on this PC";
        var part = item is { IsPartial: true } ? "the parts around markers" : "";
        Detail = state switch
        {
            DownloadItemState.Waiting => $"{head}  ·  waiting",
            DownloadItemState.Running when item!.Progress is { State: DownloadState.Reconnecting } => $"{head}  ·  reconnecting…",
            DownloadItemState.Running => $"{head}  ·  {(part.Length > 0 ? part + " " : "")}{Fraction:P0}  ·  {Pipeline.Size(item!.BytesFetched)} so far",
            DownloadItemState.CannotReach => $"{head}  ·  can't reach the library: it continues when the network changes, or press Continue",
            DownloadItemState.Failed => $"{head}  ·  failed: {item!.Error}",
            _ when item?.Progress is { Missing: > 0 } p => $"{head}  ·  Steam had already removed {p.Missing} file(s){data}",
            _ when coverage.IsComplete => $"{head}  ·  on this PC{data}",
            _ when coverage.Have > 0 => $"{head}  ·  {onPc} ({coverage.Fraction:P0}); Continue gets the rest{data}",
            _ => head,
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));   // every property
    }
}

/// <summary>
/// Downloads: lists a network library (read-only) and fetches recordings into the local chunk cache. The page holds no state of its own:
/// what is done comes from the cache, what is running comes from <see cref="AppServices.Downloads"/>.
/// </summary>
public sealed partial class DownloadsPage : Page
{
    private readonly List<DownloadRow> _rows = [];
    private LocalChunkCache? _cache;
    private DownloadQueue? _queue;
    private CancellationTokenSource? _scan;
    private CancellationTokenSource? _markerScan;
    private string _statusHead = "";
    private bool _syncing;

    public DownloadsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _scan?.Cancel();
            _markerScan?.Cancel();
            if (_queue is not null) _queue.Changed -= OnQueueChanged;
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            (_cache, _queue) = await AppServices.DownloadsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "The download cache can't be opened: " + ex.Message;
            return;
        }
        _queue.Changed += OnQueueChanged;
        LoadLibraries(AppServices.Settings.NetworkLibraries.FirstOrDefault());
    }

    private void LoadLibraries(string? select)
    {
        _syncing = true;
        var libraries = AppServices.Settings.NetworkLibraries;
        LibraryBox.ItemsSource = null;
        LibraryBox.ItemsSource = libraries.ToList();
        LibraryBox.SelectedIndex = select is null ? -1 : libraries.IndexOf(select);
        RefreshButton.IsEnabled = RemoveButton.IsEnabled = select is not null;
        _syncing = false;
        _ = ScanAsync();
    }

    private void LibraryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) _ = ScanAsync();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var path = await FirstRunCard.PickFolderAsync();
        if (path is null) return;
        var libraries = AppServices.Settings.NetworkLibraries;
        if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(path);
            AppServices.Settings.Save();
        }
        LoadLibraries(path);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryBox.SelectedItem is not string path) return;
        AppServices.Settings.NetworkLibraries.Remove(path);
        AppServices.Settings.Save();
        LoadLibraries(AppServices.Settings.NetworkLibraries.FirstOrDefault());
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private async Task ScanAsync()
    {
        _scan?.Cancel();
        _markerScan?.Cancel();
        _rows.Clear();
        RecordingList.ItemsSource = null;
        var root = LibraryBox.SelectedItem as string;
        EmptyText.Visibility = Visibility.Visible;
        EmptyText.Text = root is null ? "Add a network library to see its recordings here." : "Reading the library…";
        _statusHead = "";
        StatusText.Text = "";
        if (root is null) return;

        using var cts = new CancellationTokenSource();
        _scan = cts;
        try
        {
            var link = await LinkDetector.DetectAsync(root, cts.Token);
            var list = await Task.Run(() => RemoteLibrary.ListAsync(root, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            var id = RemoteLibrary.IdFor(root);
            _rows.AddRange(list.Select(r => new DownloadRow(id, r, TitleOf(r))));
            foreach (var row in _rows) UpdateRow(row);
            RecordingList.ItemsSource = _rows.ToList();
            EmptyText.Visibility = _rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyText.Text = "No recordings in this folder. Choose the folder that holds Steam's \"video\" and \"clips\" folders.";
            _statusHead = $"{LinkText(link)}  ·  {_rows.Count} recording{(_rows.Count == 1 ? "" : "s")}";
            RefreshStatus();
            _ = LoadMarkersAsync(root, list);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmptyText.Text = "Can't reach this library. Check the connection and your access to the share, then press Refresh.";
            PlayerLog.Write("network library failed: " + ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(_scan, cts)) _scan = null;          // cts is disposed on return: never cancel it from elsewhere
        }
    }

    private void RefreshStatus()
    {
        if (_statusHead.Length == 0 || _cache is null || _queue is null) return;
        StatusText.Text = $"{_statusHead}  ·  cache {Pipeline.Size(_cache.TotalBytes)} of {Pipeline.Size(_cache.MaxBytes)}  ·  {Pipeline.Size(_queue.TotalBytesFetched)} downloaded from the source since the app started";
    }

    /// <summary>Reads every recording's markers in the background (one read of the library's context) so "Around markers" knows which rows can use it.</summary>
    private async Task LoadMarkersAsync(string root, IReadOnlyList<RemoteRecording> list)
    {
        var cts = new CancellationTokenSource();
        _markerScan = cts;
        var rows = _rows.ToDictionary(r => r.Recording.Folder);
        await RemoteLibrary.TryLoadAllMarkersAsync(root, list, 30, (folder, markers) => DispatcherQueue.TryEnqueue(() =>
        {
            if (cts.IsCancellationRequested || !rows.TryGetValue(folder, out var row)) return;
            row.Markers = markers;
            UpdateRow(row);
        }), cts.Token);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (cts.IsCancellationRequested) return;
            foreach (var row in _rows.Where(r => r.Markers is null))
            {
                row.MarkersFailed = true;
                UpdateRow(row);
            }
        });
    }

    private static string LinkText(LinkKind link) => link switch
    {
        LinkKind.Wired => "Wired link",
        LinkKind.Wireless => "Wireless link",
        LinkKind.Local => "On this PC",
        _ => "Network link",
    };

    private static string TitleOf(RemoteRecording r)
    {
        var kind = r.Name.Kind == RecordingKind.CacheSession ? "Cache session" : "Clip";
        var id = r.Name.GameId?.ToString(CultureInfo.InvariantCulture);
        var game = id is null ? null : AppServices.Library?.Current?.Games.FirstOrDefault(g => g.GameId == id)?.Name;
        return $"{game ?? (id is null ? "Downloaded clip" : "Game " + id)}  ·  {kind}";
    }

    private ChunkKey[] KeysOf(DownloadRow row)
    {
        var folder = row.Recording.Folder;
        return [.. new[] { 0 }.Concat(row.Recording.Media.Chunks()).SelectMany(c => new[] { new ChunkKey(row.LibraryId, folder, 0, c), new ChunkKey(row.LibraryId, folder, 1, c) })];
    }

    private void UpdateRow(DownloadRow row)
    {
        if (_cache is null || _queue is null) return;
        var keys = KeysOf(row);
        var folder = row.Recording.Folder;
        var item = _queue.Items.FirstOrDefault(i => i.LibraryId == row.LibraryId && i.Recording.Folder == folder);
        var coverage = DownloadCoverage.Of(row.Recording.Media, c =>
            _cache.Contains(new ChunkKey(row.LibraryId, folder, 0, c)) && _cache.Contains(new ChunkKey(row.LibraryId, folder, 1, c)));
        row.Update(item, keys.Count(_cache.Contains), coverage, _cache.IsPinned(row.LibraryId, folder));
    }

    private void OnQueueChanged(DownloadItem item)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var row in _rows.Where(r => r.LibraryId == item.LibraryId && r.Recording.Folder == item.Recording.Folder)) UpdateRow(row);
            RefreshStatus();
        });
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DownloadRow row } || _queue is null) return;
        var item = _queue.Items.FirstOrDefault(i => i.LibraryId == row.LibraryId && i.Recording.Folder == row.Recording.Folder);
        if (item is { State: DownloadItemState.Waiting or DownloadItemState.Running }) _queue.Cancel(item);
        else
        {
            MaybeShowMeteredNotice();
            _queue.Enqueue(row.LibraryId, row.Recording);
        }
        UpdateRow(row);
    }

    /// <summary>Download only the chunks within 30 s of each marker (plan §4.3).</summary>
    private void Around_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DownloadRow row } || _queue is null || row.Markers is not { } markers) return;
        var moments = markers.Where(m => m.Alignment != MarkerAlignment.None).Select(m => (m.StartSeconds, m.EndSeconds ?? m.StartSeconds));
        var chunks = ChunkPlan.AroundMoments(row.Recording.Media, moments, AroundSeconds, AroundSeconds);
        if (chunks.Count == 0)
        {
            AppServices.Toast("Nothing near the markers", "Steam has already removed the parts around this recording's markers.", InfoBarSeverity.Warning);
            return;
        }
        MaybeShowMeteredNotice();
        _queue.Enqueue(row.LibraryId, row.Recording, chunks);
        UpdateRow(row);
    }

    private const double AroundSeconds = 30;

    /// <summary>The one notice on a metered network: no cap, no prompt, no pause (plan §4.3).</summary>
    private void MaybeShowMeteredNotice()
    {
        if (AppServices.Metered.ShouldShow(AppServices.CurrentConnectionCost(), AppServices.Settings.HideMeteredNotice))
            MeteredBar.IsOpen = true;
    }

    private void DontShowMetered_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.HideMeteredNotice = true;
        AppServices.Settings.Save();
        MeteredBar.IsOpen = false;
    }

    private async void Keep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: DownloadRow row } button || _cache is null) return;
        if (button.IsChecked == true) await _cache.PinAsync(row.LibraryId, row.Recording.Folder);
        else await _cache.UnpinAsync(row.LibraryId, row.Recording.Folder);
        UpdateRow(row);
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DownloadRow row } || AppServices.DownloadsFolder is not { } root) return;
        var folder = Path.Combine(root, row.LibraryId, row.Recording.Folder);
        try
        {
            var scan = await Task.Run(() => Scanner.ScanAsync(folder));
            var markers = row.Markers ?? (LibraryBox.SelectedItem is string library ? await RemoteLibrary.TryLoadMarkersAsync(library, row.Recording) : []);
            if (scan.Media is not { ChunkCount: > 0 } media)
            {
                AppServices.Toast("Nothing to play yet", "Wait for the first chunks to arrive.");
                return;
            }
            // Local files that nothing trims: neither live nor Steam's rolling cache, however they looked on the other PC.
            AppServices.Window?.NavigateToPlayer(new PlayerRequest($"{row.LibraryId}/{row.Recording.Folder}", Media: media with { Live = false, Temporary = false }, Title: row.Title, Markers: markers));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Toast("Can't play this download", ex.Message, InfoBarSeverity.Error);
        }
    }
}
