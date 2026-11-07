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
        // --selftest / --bench run headless before any window exists and exit with their result code.
        if (SteamPoweredPlayer.Core.Diagnostics.DiagnosticsCli.TryParse(Environment.GetCommandLineArgs().Skip(1).ToArray()) is { } diagnostics)
            Environment.Exit(SteamPoweredPlayer.Core.Diagnostics.DiagnosticsCli.Run(diagnostics));
        AppServices.Init(AppOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        _window = new MainWindow();
        _window.Activate();
    }
}
