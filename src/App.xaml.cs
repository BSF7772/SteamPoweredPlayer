// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using SteamPoweredPlayer.Services;

namespace SteamPoweredPlayer;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // WinExe, unpackaged: arguments come from the process command line.
        AppServices.Init(AppOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        PlayerHost.Start();        // mpv + renderer on a worker thread, so the first open is warm
        _window = new MainWindow();
        _window.Activate();
    }
}
