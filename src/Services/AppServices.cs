// SPDX-License-Identifier: GPL-3.0-or-later
using FastEncoder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Net;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// Command line: <c>--sample</c> (synthetic library), <c>--first-run</c> (pretend nothing is confirmed), <c>--theme light|dark</c>,
/// <c>--page library|settings|player</c>, <c>--data &lt;dir&gt;</c> (use and confirm that data folder for this run only; settings.json is neither read nor written).
/// </summary>
public sealed record AppOptions(bool Sample, bool FirstRun, ElementTheme Theme, string Page, string? DataDir = null)
{
    public static AppOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var theme = Value("--theme")?.ToLowerInvariant() switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        return new(args.Contains("--sample"), args.Contains("--first-run"), theme, Value("--page")?.ToLowerInvariant() ?? "library", Value("--data"));
    }
}

/// <summary>What Steam's own config says (read-only). Everything is optional: Steam may not be installed on this machine.</summary>
public sealed record SteamInfo(bool SteamFound, string? RecordingsPath, TimeSpan? MaxKeep, SteamPaths? Paths = null, uint? AccountId = null);

/// <summary>The Player page receives this as its navigation parameter. <see cref="RecordingKey"/> is <c>RecordingEntry.RelativeFolder</c>.</summary>
/// <param name="Media">The media to play when the recording is not in the library, e.g. a download.</param>
/// <param name="Title">The heading for such a recording.</param>
/// <param name="Markers">Its markers, when known.</param>
public sealed record PlayerRequest(string RecordingKey, double? StartSeconds = null, SessionMedia? Media = null, string? Title = null, IReadOnlyList<Marker>? Markers = null);

/// <summary>Small service locator: the shell is tiny, and pages need the same few objects.</summary>
public static class AppServices
{
    public static AppOptions Options { get; private set; } = new(false, false, ElementTheme.Default, "library");
    public static AppSettings Settings { get; private set; } = new();
    public static SteamInfo Steam { get; private set; } = new(false, null, null);
    public static ILibrarySource? Library { get; private set; }
    public static DataFolder? DataFolder { get; private set; }
    public static MainWindow? Window { get; set; }

    /// <summary>Raised when a library source was created and before it starts, so the shell can subscribe to its first events.</summary>
    public static event Action<ILibrarySource>? LibraryCreated;

    public static void Init(AppOptions options)
    {
        Options = options;
        Settings = options.Sample ? new AppSettings() : options.DataDir is { } dir ? new AppSettings { DataFolder = dir } : AppSettings.Load();
    }

    /// <summary>Reads Steam's config (never writes). In sample mode returns made-up values.</summary>
    public static async Task<SteamInfo> LoadSteamAsync(CancellationToken ct = default)
    {
        if (Options.Sample)
            return Steam = new SteamInfo(true, @"C:\Sample\Recordings", TimeSpan.FromHours(2));
        try
        {
            string? recordings = null;
            TimeSpan? keep = null;
            uint? account = null;
            var paths = SteamPaths.FromRegistry();
            if (paths is not null)
            {
                var user = await paths.ReadActiveUserAsync(ct).ConfigureAwait(false);
                if (user is not null)
                {
                    account = user.AccountId;
                    var cfg = await SteamConfig.LoadAsync(paths.LocalConfig(user.AccountId), ct).ConfigureAwait(false);
                    recordings = cfg.BackgroundRecordPath;
                    keep = cfg.BackgroundMaxKeep;
                }
            }
            recordings ??= Settings.RecordingsFolder;
            return Steam = new SteamInfo(paths is not null, recordings, keep, paths, account);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Steam = new SteamInfo(false, Settings.RecordingsFolder, null);
        }
    }

    /// <summary>The data folder to use now: the confirmed one, or (sample / <c>--first-run</c> aside) none yet.</summary>
    public static bool NeedsFirstRun => !FirstRunDone && ((!Options.Sample && Settings.DataFolder is null) || Options.FirstRun);

    /// <summary>Set when the first-run card was confirmed in this process (<c>--first-run</c> keeps asking otherwise).</summary>
    public static bool FirstRunDone { get; set; }

    /// <summary>The real library source (null in sample mode), for the members only it has.</summary>
    public static LibraryServiceSource? Service => Library as LibraryServiceSource;

    /// <summary>Background trickplay generation (null in sample mode). The player calls <c>Pause()</c> / <c>Resume()</c> around opening a recording.</summary>
    public static TrickplayQueue? Trickplay { get; private set; }

    /// <summary>The game selected in the Library page; its recordings get their trickplay first.</summary>
    public static string? PreferredGameId { get; set; }

    /// <summary>Creates the library source for the current settings and starts it (the saved index is shown at once, the scan runs in the background).</summary>
    public static async Task StartLibraryAsync()
    {
        Trickplay?.Dispose();
        Trickplay = null;
        if (Library is IDisposable d) d.Dispose();
        Library = null;
        DataFolder = null;

        if (Options.Sample)
        {
            Library = new SampleLibrarySource();
        }
        else
        {
            // Unconfirmed (first run) = in memory: nothing is written until the card's Confirm calls ConfirmDataFolder.
            if (!NeedsFirstRun && Settings.DataFolder is { } folder && Steam.RecordingsPath is { } brp && Core.Library.DataFolder.Validate(folder, brp).IsValid)
                DataFolder = Core.Library.DataFolder.Open(folder, brp);
            var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var source = new LibraryServiceSource(ui, Steam.RecordingsPath, Steam.Paths, Steam.AccountId, DataFolder);
            Library = source;
            Trickplay = new TrickplayQueue(source, () => Settings.Decoding, () => PreferredGameId);
        }
        LibraryCreated?.Invoke(Library);
        await Library.StartAsync().ConfigureAwait(true);
        if (PlayerHost.Instance is { } player) _ = player.PreWarmAsync(Library);
    }

    /// <summary>The first-run card or Settings picked a valid data folder: save the choice and let the running library start writing there.</summary>
    public static void ConfirmDataFolder(string path)
    {
        if (Steam.RecordingsPath is not { } brp) return;
        var folder = Core.Library.DataFolder.Open(path, brp);
        Settings.DataFolder = folder.Root;
        Settings.Save();
        FirstRunDone = true;
        DataFolder = folder;
        Service?.AttachDataFolder(folder);
        Trickplay?.Kick();
    }

    public static void Toast(string title, string? message = null, InfoBarSeverity severity = InfoBarSeverity.Informational, int seconds = 6) =>
        Window?.ShowToast(title, message, severity, seconds);

    // ---- downloads --------------------------------------------------------------------------------------------------------

    private static readonly SemaphoreSlim s_downloadsGate = new(1, 1);
    private static LocalChunkCache? s_cache;
    private static DownloadQueue? s_downloads;

    /// <summary>Where downloaded chunks live, per Windows user; never in Steam's folder. Null until <see cref="DownloadsAsync"/> ran.</summary>
    public static string? DownloadsFolder { get; private set; }

    /// <summary>The app's one chunk cache and download queue, opened on first use. They outlive pages.</summary>
    public static async Task<(LocalChunkCache Cache, DownloadQueue Queue)> DownloadsAsync()
    {
        await s_downloadsGate.WaitAsync();
        try
        {
            if (s_cache is null || s_downloads is null)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPoweredPlayer", "downloads");
                Directory.CreateDirectory(dir);
                s_cache = await LocalChunkCache.OpenAsync(dir, Math.Max(1, Settings.DownloadCacheGb) * (1L << 30));
                s_downloads = new DownloadQueue(s_cache, new SharedFolderFiles());
                s_downloads.Changed += OnDownloadChanged;
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => s_downloads.NetworkChanged();
                DownloadsFolder = dir;
            }
            return (s_cache, s_downloads);
        }
        finally { s_downloadsGate.Release(); }
    }

    private static void OnDownloadChanged(DownloadItem item)
    {
        if (item.State is DownloadItemState.Done or DownloadItemState.Failed)
            Window?.DispatcherQueue.TryEnqueue(() =>
            {
                if (item.State == DownloadItemState.Done) Toast("Download finished", "It plays from this PC now.", InfoBarSeverity.Success);
                else Toast("Download failed", item.Error, InfoBarSeverity.Error);
            });
    }

    // ---- exports --------------------------------------------------------------------------------------------------------

    private static ExportQueue? s_exports;
    private static Taskbar? s_taskbar;

    /// <summary>Fires on any thread after a queued export changed.</summary>
    public static event Action? ExportsChanged;

    /// <summary>The app's one export queue: outlives pages, shows progress on the taskbar and reports each result as a toast.</summary>
    public static ExportQueue Exports
    {
        get
        {
            if (s_exports is null)
            {
                s_exports = new ExportQueue();
                s_exports.Changed += OnExportChanged;
            }
            return s_exports;
        }
    }

    private static void OnExportChanged(ExportItem item)
    {
        ExportsChanged?.Invoke();
        if (Window is not { } window) return;
        window.DispatcherQueue.TryEnqueue(() =>
        {
            s_taskbar ??= new Taskbar(window.Hwnd);
            if (Exports.OverallFraction is { } fraction) s_taskbar.Set(Taskbar.State.Normal, fraction);
            else s_taskbar.Set(Taskbar.State.None);
            switch (item.State)
            {
                case ExportItemState.Done when item.Result is { } result:
                    Toast("Export finished", $"{Path.GetFileName(result.Path)}  ·  {Pipeline.Size(result.Bytes)}", InfoBarSeverity.Success);
                    s_taskbar.Flash();
                    break;
                case ExportItemState.Cancelled:
                    Toast("Export cancelled", null, InfoBarSeverity.Informational, 3);
                    break;
                case ExportItemState.Failed when item.Error is { } error:
                    PlayerLog.Write("export failed: " + error.GetType().Name);
                    Toast("Export failed", error is ChunkGoneException gone ? ExportService.DescribeGone(item.Job.Media, gone) : error.Message, InfoBarSeverity.Error);
                    break;
            }
        });
    }
}
