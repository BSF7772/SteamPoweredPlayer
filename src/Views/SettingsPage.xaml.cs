// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamPoweredPlayer.Core;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Services;

namespace SteamPoweredPlayer.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;
        DecodeButtons.SelectedIndex = AppServices.Settings.Decoding == DecodeMode.Cpu ? 1 : 0;
        GpuOption.Content = GpuInfo.Name is { } gpu ? $"GPU (auto) · {gpu}" : "GPU (auto)";
        _loading = false;
        if (GpuInfo.Name is null) _ = GpuInfo.LoadAsync().ContinueWith(_ => DispatcherQueue.TryEnqueue(() => { if (GpuInfo.Name is { } g) GpuOption.Content = $"GPU (auto) · {g}"; }), TaskScheduler.Default);

        ShowFolders();
        ChangeDataButton.IsEnabled = !AppServices.Options.Sample && AppServices.Steam.RecordingsPath is not null;

        _loading = true;
        CacheCapBox.Value = Math.Max(1, AppServices.Settings.DownloadCacheGb);
        _loading = false;
        _ = ShowCacheAsync();
    }

    private async Task ShowCacheAsync()
    {
        if (AppServices.Options.Sample)
        {
            CacheSizeText.Text = "";
            CacheFolderText.Text = "(sample library, nothing on disk)";
            return;
        }
        try
        {
            var (bytes, folder) = await AppServices.DownloadCacheInfoAsync();
            CacheSizeText.Text = $"In use: {FastEncoder.Core.Pipeline.Size(bytes)}";
            CacheFolderText.Text = folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CacheSizeText.Text = "The download cache can't be opened: " + e.Message;
        }
    }

    private void CacheCap_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue))                                  // emptied or invalid text: back to the saved value
        {
            sender.Value = Math.Max(1, AppServices.Settings.DownloadCacheGb);
            return;
        }
        AppServices.Settings.DownloadCacheGb = (int)Math.Round(args.NewValue);
        AppServices.Settings.Save();
        AppServices.ApplyDownloadCacheCap();
    }

    private void ShowFolders()
    {
        DataFolderText.Text = AppServices.DataFolder?.Root ?? AppServices.Settings.DataFolder ?? (AppServices.Options.Sample ? "(sample library, nothing on disk)" : "Not chosen yet");
        RecordingsText.Text = AppServices.Steam.RecordingsPath ?? "Not found. Steam may not be installed, or Game Recording is off.";
        KeepText.Text = AppServices.Steam.MaxKeep is { } k ? "Steam keeps the last " + Fmt.Span(k) + " in the background cache." : "";
        OpenDataButton.IsEnabled = AppServices.DataFolder is { } d && Directory.Exists(d.Root);
        OpenRecordingsButton.IsEnabled = AppServices.Steam.RecordingsPath is { } r && Directory.Exists(r);
    }

    private void Decode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.Decoding = DecodeButtons.SelectedIndex == 1 ? DecodeMode.Cpu : DecodeMode.Gpu;
        AppServices.Settings.Save();
        AppServices.Window?.RefreshChips();   // the next trickplay job and the next opened recording read the setting
    }

    /// <summary>Same rule as the first-run card: the picked folder is validated against Steam's recording path by Core.</summary>
    private async void ChangeData_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Steam.RecordingsPath is not { } brp) return;
        var path = await FirstRunCard.PickFolderAsync();
        if (path is null) return;
        var check = DataFolder.Validate(path, brp);
        DataProblem.IsOpen = !check.IsValid;
        if (!check.IsValid)
        {
            DataProblem.Message = FirstRunCard.Explain(check.Problem);
            return;
        }
        AppServices.ConfirmDataFolder(check.Path!);
        ShowFolders();
        AppServices.Window?.RefreshChips();
        AppServices.Toast("Library folder changed", "The index and thumbnails are saved there from now on. The old folder is left as it was.", InfoBarSeverity.Success, 6);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Reveal(AppServices.DataFolder?.Root);
    private void OpenRecordings_Click(object sender, RoutedEventArgs e) => Reveal(AppServices.Steam.RecordingsPath);

    private static void Reveal(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
    }
}
