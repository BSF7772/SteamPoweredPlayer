// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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
        _loading = false;

        DataFolderText.Text = AppServices.DataFolder?.Root ?? AppServices.Settings.DataFolder ?? (AppServices.Options.Sample ? "(sample library, nothing on disk)" : "Not chosen yet");
        RecordingsText.Text = AppServices.Steam.RecordingsPath ?? "Not found. Steam may not be installed, or Game Recording is off.";
        KeepText.Text = AppServices.Steam.MaxKeep is { } k ? "Steam keeps the last " + Fmt.Span(k) + " in the background cache." : "";
    }

    private void Decode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.Decoding = DecodeButtons.SelectedIndex == 1 ? DecodeMode.Cpu : DecodeMode.GpuAuto;
        AppServices.Settings.Save();
        AppServices.Window?.RefreshChips();
    }
}
