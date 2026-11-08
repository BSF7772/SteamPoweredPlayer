// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;

namespace SteamPoweredPlayer.Services;

/// <summary>Opens an exported file with its default app, for the toasts that name one. A file that has moved gives an error toast.</summary>
internal static class FileOpener
{
    /// <summary>For <c>--uicheck</c>: receives the path instead of starting an app.</summary>
    internal static Action<string>? Intercept { get; set; }

    public static void Open(string path)
    {
        if (Intercept is { } intercept) { intercept(path); return; }
        if (!File.Exists(path))
        {
            AppServices.Toast("That file is gone", $"{Path.GetFileName(path)} was moved or deleted.", InfoBarSeverity.Error);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            PlayerLog.Write("open file failed: " + e.GetType().Name);
            AppServices.Toast("Couldn't open the file", e.Message, InfoBarSeverity.Error);
        }
    }
}
