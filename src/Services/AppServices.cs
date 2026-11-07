// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Services;

/// <summary>Command line: <c>--sample</c> (synthetic library), <c>--first-run</c> (pretend nothing is confirmed), <c>--theme light|dark</c>, <c>--page library|settings|player</c>.</summary>
public sealed record AppOptions(bool Sample, bool FirstRun, ElementTheme Theme, string Page)
{
    public static AppOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var theme = Value("--theme")?.ToLowerInvariant() switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        return new(args.Contains("--sample"), args.Contains("--first-run"), theme, Value("--page")?.ToLowerInvariant() ?? "library");
    }
}

/// <summary>What Steam's own config says (read-only). Everything is optional: Steam may not be installed on this machine.</summary>
public sealed record SteamInfo(bool SteamFound, string? RecordingsPath, TimeSpan? MaxKeep);

/// <summary>The Player page receives this as its navigation parameter. <see cref="RecordingKey"/> is <c>RecordingEntry.RelativeFolder</c>.</summary>
public sealed record PlayerRequest(string RecordingKey, double? StartSeconds = null);

/// <summary>Small service locator: the shell is tiny, and pages need the same few objects.</summary>
public static class AppServices
{
    public static AppOptions Options { get; private set; } = new(false, false, ElementTheme.Default, "library");
    public static AppSettings Settings { get; private set; } = new();
    public static SteamInfo Steam { get; private set; } = new(false, null, null);
    public static ILibrarySource? Library { get; private set; }
    public static DataFolder? DataFolder { get; private set; }
    public static MainWindow? Window { get; set; }

    public static void Init(AppOptions options)
    {
        Options = options;
        Settings = options.Sample ? new AppSettings() : AppSettings.Load();
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
            var paths = SteamPaths.FromRegistry();
            if (paths is not null)
            {
                var user = await paths.ReadActiveUserAsync(ct).ConfigureAwait(false);
                if (user is not null)
                {
                    var cfg = await SteamConfig.LoadAsync(paths.LocalConfig(user.AccountId), ct).ConfigureAwait(false);
                    recordings = cfg.BackgroundRecordPath;
                    keep = cfg.BackgroundMaxKeep;
                }
            }
            recordings ??= Settings.RecordingsFolder;
            return Steam = new SteamInfo(paths is not null, recordings, keep);
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

    /// <summary>Creates the library source for the current settings and starts it.</summary>
    public static async Task StartLibraryAsync()
    {
        if (Library is IDisposable d) d.Dispose();
        Library = null;
        DataFolder = null;

        if (Options.Sample)
        {
            Library = new SampleLibrarySource();
        }
        else if (Settings.DataFolder is { } folder && Steam.RecordingsPath is { } brp)
        {
            var check = Core.Library.DataFolder.Validate(folder, brp);
            if (check.IsValid)
            {
                DataFolder = Core.Library.DataFolder.Open(folder, brp);
                Library = new IndexFileLibrarySource(DataFolder);
            }
        }
        if (Library is not null)
        {
            await Library.StartAsync().ConfigureAwait(true);
            if (PlayerHost.Instance is { } player) _ = player.PreWarmAsync(Library);
        }
    }

    public static void Toast(string title, string? message = null, InfoBarSeverity severity = InfoBarSeverity.Informational, int seconds = 6) =>
        Window?.ShowToast(title, message, severity, seconds);
}
