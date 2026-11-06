// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;

namespace S1App;

public partial class App : Application
{
    public static MainWindow? Main { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { Log.W("UNHANDLED " + e.Exception); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // WinExe: args come from the process command line (unpackaged), not from LaunchActivatedEventArgs.
        var o = Options.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        Main = new MainWindow(o);
        Main.Activate();
    }
}
