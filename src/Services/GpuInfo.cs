// SPDX-License-Identifier: GPL-3.0-or-later
using Vortice.DXGI;

namespace SteamPoweredPlayer.Services;

/// <summary>The description of DXGI adapter 0 (the one D3D11 uses by default), read once off the UI thread.</summary>
public static class GpuInfo
{
    private static string? _name;
    private static Task<string?>? _task;

    /// <summary>The adapter name, or null until <see cref="LoadAsync"/> finished or when none can be read.</summary>
    public static string? Name => _name;

    public static Task<string?> LoadAsync() => _task ??= Task.Run(() =>
    {
        try
        {
            if (DXGI.CreateDXGIFactory1<IDXGIFactory1>(out var factory).Failure || factory is null) return null;
            using (factory)
            {
                if (factory.EnumAdapters1(0, out var adapter).Failure || adapter is null) return null;
                using (adapter)
                    return _name = adapter.Description1.Description.Trim();
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    });
}
