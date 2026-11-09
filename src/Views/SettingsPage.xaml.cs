// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamPoweredPlayer.Core;
using SteamPoweredPlayer.Core.Diagnostics;
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
        PhoneCard.Visibility = PhoneDeploy.FindScript(AppContext.BaseDirectory) is not null ? Visibility.Visible : Visibility.Collapsed;
        DeployPhoneButton.IsEnabled = !s_deploying;
        DeployRing.IsActive = s_deploying;
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

    /// <summary>Arguments added to the deploy script; <c>--uicheck</c> uses <c>-CheckOnly</c>, so it drives the real button without building or installing.</summary>
    internal static IReadOnlyList<string> DeployArguments { get; set; } = [];

    /// <summary>The script runs on, and its result shows, even after leaving this page: <see cref="s_deploying"/> stops a second run meanwhile.</summary>
    private static bool s_deploying;

    private async void DeployPhone_Click(object sender, RoutedEventArgs e)
    {
        if (s_deploying || PhoneDeploy.FindScript(AppContext.BaseDirectory) is not { } script) return;
        s_deploying = true;
        DeployPhoneButton.IsEnabled = false;
        DeployRing.IsActive = true;
        DeployStatus.Text = "Starting…";
        try
        {
            var code = await PhoneDeploy.RunAsync(script, DeployArguments, line => DispatcherQueue.TryEnqueue(() => DeployStatus.Text = line), CancellationToken.None);
            if (code != 0) AppServices.Toast("Couldn't install on the phone", DeployStatus.Text, InfoBarSeverity.Warning, 0);
            else if (DeployArguments.Count == 0) AppServices.Toast("Installed on the phone", "You can unplug it.", InfoBarSeverity.Success, 6);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DeployStatus.Text = "Couldn't run the script: " + ex.Message;
        }
        finally
        {
            s_deploying = false;
            DeployPhoneButton.IsEnabled = true;
            DeployRing.IsActive = false;
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Reveal(AppServices.DataFolder?.Root);
    private void OpenRecordings_Click(object sender, RoutedEventArgs e) => Reveal(AppServices.Steam.RecordingsPath);

    private static void Reveal(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
    }
}
