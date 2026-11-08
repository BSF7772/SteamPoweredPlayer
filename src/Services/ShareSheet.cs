// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// Windows' own share sheet for a throwaway clip. The clip lives in a private temp folder and is removed when the sheet is cancelled, shortly
/// after a target took it (a target can keep reading after it reports success), or at the next share or the app's start and close at the latest.
/// </summary>
internal static class ShareSheet
{
    private static readonly TimeSpan KeepAfterShared = TimeSpan.FromMinutes(2);
    private static DataTransferManager? s_manager;
    private static string? s_path;

    /// <summary>The sheet asked for the clip and got it: for a manual <c>--uicheck</c> with <c>SPP_UICHECK_REAL_SHARE=1</c>.</summary>
    internal static bool ClipProvided { get; private set; }

    /// <summary>For <c>--uicheck</c>: receives the finished clip instead of opening the sheet, which automation couldn't close.</summary>
    internal static Action<string>? Intercept { get; set; }

    private static string Root => Path.Combine(Path.GetTempPath(), "SteamPoweredPlayer-share");

    /// <summary>A new, empty folder for the next clip; the clips of earlier shares go.</summary>
    public static string NewFolder()
    {
        Sweep();
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Removes this app's own temp clips. Nothing else lives under <see cref="Root"/>.</summary>
    public static void Sweep()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var folder in Directory.EnumerateDirectories(Root)) Remove(folder);
    }

    private static void Remove(string? folder)
    {
        if (folder is null) return;
        try { Directory.Delete(folder, recursive: true); }                  // a target still reading it: the next sweep gets it
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Opens the sheet for <paramref name="path"/>. Call on the UI thread.</summary>
    public static void Show(nint hwnd, string path)
    {
        if (Intercept is { } intercept) { intercept(path); return; }
        s_path = path;
        if (s_manager is null)
        {
            s_manager = DataTransferManagerInterop.GetForWindow(hwnd);
            s_manager.DataRequested += OnDataRequested;
        }
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
    }

    private static async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            if (s_path is not { } path) return;
            var data = args.Request.Data;
            data.Properties.Title = Path.GetFileName(path);
            data.SetStorageItems([await StorageFile.GetFileFromPathAsync(path)]);
            ClipProvided = true;
            var folder = Path.GetDirectoryName(path);
            data.ShareCanceled += (_, _) => Remove(folder);
            data.ShareCompleted += async (_, _) =>
            {
                await Task.Delay(KeepAfterShared).ConfigureAwait(false);
                Remove(folder);
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            args.Request.FailWithDisplayText("The clip couldn't be read.");
            PlayerLog.Write("share failed: " + e.GetType().Name);
        }
        finally { deferral.Complete(); }
    }
}
