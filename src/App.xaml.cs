// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using SteamPoweredPlayer.Core.Diagnostics;
using SteamPoweredPlayer.Services;

namespace SteamPoweredPlayer;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // WinExe, unpackaged: arguments come from the process command line.
        // --selftest / --bench / --realcheck run headless before any window exists and exit with their result code.
        // --uicheck starts a watched child (--uicheck-child) that drives the real window (Services/UiCheck).
        RealCheck.Formatter = Views.Player.FrameTimecode.Instance;      // --realcheck checks the Player's own timecode
        var cli = DiagnosticsCli.TryParse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        if (cli is { Command: DiagnosticsCommand.UiCheck, Child: false }) Environment.Exit(UiCheck.RunParent(cli));
        if (cli is { Command: not DiagnosticsCommand.UiCheck }) Environment.Exit(DiagnosticsCli.Run(cli));
        AppServices.Init(AppOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        PlayerHost.Start();        // mpv + renderer on a worker thread, so the first open is warm
        var main = new MainWindow();
        _window = main;
        _window.Activate();
        if (cli is { Command: DiagnosticsCommand.UiCheck, Child: true }) UiCheck.Start(main, cli);
    }
}
