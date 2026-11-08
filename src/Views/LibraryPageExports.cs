// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Services;
using SteamPoweredPlayer.Views.Player;

namespace SteamPoweredPlayer.Views;

/// <summary>One row of the Exports card.</summary>
public sealed record ExportRow(string Path, string Name, string Meta)
{
    public static ExportRow From(ExportFile f) => new(f.Path, System.IO.Path.GetFileNameWithoutExtension(f.Name),
        Fmt.Bytes(f.SizeBytes) + " · " + Fmt.DateTime(f.ModifiedUtc));
}

/// <summary>The EXPORTS card (files of the export folder that belong to the selected game) and the cache strip's Save clip.</summary>
public sealed partial class LibraryPage
{
    private FileSystemWatcher? _exportWatcher;
    private string? _watchedFolder;
    private string? _exportGame;
    private int _exportRefreshQueued;
    private readonly DispatcherQueue _uiQueue = DispatcherQueue.GetForCurrentThread();

    /// <summary>Shows the exports of <paramref name="gameName"/> (null: none) and watches the export folder for new ones.</summary>
    private void ShowExports(string? gameName)
    {
        _exportGame = gameName;
        WatchExportFolder(AppServices.Settings.ExportFolder);
        RefreshExports();
    }

    private void RefreshExports()
    {
        var folder = AppServices.Settings.ExportFolder;
        var rows = _exportGame is null ? [] : ExportFolderListing.ForGame(ExportFolderListing.List(folder), _exportGame).Select(ExportRow.From).ToList();
        ExportRepeater.ItemsSource = rows;
        ExportRepeater.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ExportsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportCount.Text = rows.Count == 0 ? "" : $"· {rows.Count}";
    }

    private void WatchExportFolder(string? folder)
    {
        if (string.Equals(folder, _watchedFolder, StringComparison.OrdinalIgnoreCase) && (_exportWatcher is not null || folder is null)) return;
        StopWatchingExports();
        _watchedFolder = folder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            var w = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            w.Created += ExportFolder_Changed;
            w.Deleted += ExportFolder_Changed;
            w.Renamed += ExportFolder_Changed;
            w.Changed += ExportFolder_Changed;
            w.Error += ExportFolder_Error;
            w.EnableRaisingEvents = true;
            _exportWatcher = w;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { _exportWatcher = null; }
    }

    private void StopWatchingExports()
    {
        var w = _exportWatcher;
        _exportWatcher = null;
        if (w is null) return;
        w.EnableRaisingEvents = false;
        w.Created -= ExportFolder_Changed;
        w.Deleted -= ExportFolder_Changed;
        w.Renamed -= ExportFolder_Changed;
        w.Changed -= ExportFolder_Changed;
        w.Error -= ExportFolder_Error;
        w.Dispose();
    }

    // Watcher events arrive on a thread-pool thread: coalesce a burst into one refresh on the UI thread.
    private void ExportFolder_Changed(object sender, FileSystemEventArgs e)
    {
        if (e.Name is { } n && n.StartsWith(ExportFolderListing.WorkingPrefix, StringComparison.OrdinalIgnoreCase)) return;
        QueueExportRefresh();
    }

    /// <summary>The folder vanished or the buffer overflowed: drop the watcher (it cannot recover) and show what is there now.</summary>
    private void ExportFolder_Error(object sender, ErrorEventArgs e)
    {
        _uiQueue.TryEnqueue(() =>
        {
            _watchedFolder = null;
            StopWatchingExports();
            RefreshExports();
        });
    }

    private void QueueExportRefresh()
    {
        if (Interlocked.Exchange(ref _exportRefreshQueued, 1) == 1) return;
        _uiQueue.TryEnqueue(() =>
        {
            Volatile.Write(ref _exportRefreshQueued, 0);
            if (_exportWatcher is not null) RefreshExports();
        });
    }

    private void ExportOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || !File.Exists(path)) { RefreshExports(); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void ExportReveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        if (!File.Exists(path)) { RefreshExports(); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>
    /// Opens the Player on the live (else newest) cache session, 30 s before its end: the Player's Export card has the Save clip button
    /// that cuts it. The strip has no selection of its own; a block click already opens that session from its head.
    /// </summary>
    private void SaveClip_Click(object sender, RoutedEventArgs e)
    {
        var s = _sessions.FirstOrDefault(x => x.Live) ?? _sessions.LastOrDefault();
        if (s is null) return;
        var start = Math.Max(0, SpanOf(s).Seconds - 30);
        AppServices.Window?.NavigateToPlayer(new PlayerRequest(s.RelativeFolder, start));
    }
}
