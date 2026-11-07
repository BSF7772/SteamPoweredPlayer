// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>
/// An invisible top-level window (never shown, never activated) that mpv can render into with <c>--wid</c>. It lives on its own thread
/// with a message loop: a window dies with the thread that created it, and mpv's child window talks to its parent's thread, so a
/// thread-pool thread would not do. <c>--bench</c> uses it to measure hardware decode with <c>hwdec=d3d11va</c> (zero-copy into mpv's
/// own D3D11 renderer), which needs a real video output; the XAML swap chain of the Player page is still not part of it.
/// </summary>
public sealed partial class HiddenWindow : IDisposable
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WM_QUIT = 0x0012;

    private readonly Thread _thread;
    private nint _hwnd;
    private uint _threadId;
    private int _disposed;

    private HiddenWindow(int width, int height)
    {
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();
                _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, "Static", "", WS_POPUP, -32000, -32000, width, height, 0, 0, 0, 0);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _hwnd = 0; }
            finally { ready.Set(); }
            if (_hwnd == 0) return;
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            DestroyWindow(_hwnd);
        }) { IsBackground = true, Name = "spp-hidden-window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        ready.Dispose();
    }

    /// <summary>The window handle as a decimal string, the format of mpv's <c>wid</c> option.</summary>
    public string Wid => _hwnd.ToString(CultureInfo.InvariantCulture);

    /// <summary>Creates the window, or returns null when Windows refuses (no desktop, a service session).</summary>
    public static HiddenWindow? TryCreate(int width = 1280, int height = 720)
    {
        var w = new HiddenWindow(width, height);
        if (w._hwnd != 0) return w;
        w.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_hwnd != 0 && _threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref Msg msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
