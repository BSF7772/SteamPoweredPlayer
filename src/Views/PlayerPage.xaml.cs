// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamPoweredPlayer.Services;

namespace SteamPoweredPlayer.Views;

/// <summary>Placeholder. Navigation parameter: <see cref="PlayerRequest"/> (RecordingKey = RecordingEntry.RelativeFolder).</summary>
public sealed partial class PlayerPage : Page
{
    public PlayerRequest? Request { get; private set; }

    public PlayerPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Request = e.Parameter as PlayerRequest;
        KeyText.Text = Request?.RecordingKey ?? "";
    }

    private void Back_Click(object sender, RoutedEventArgs e) => AppServices.Window?.NavigateToLibrary();
}
