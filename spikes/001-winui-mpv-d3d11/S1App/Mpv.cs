// SPDX-License-Identifier: GPL-3.0-or-later
// Minimal libmpv binding for spike S1 (client API + render API, D3D11 backend). Seed for src/Core/Playback.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace S1App;

[StructLayout(LayoutKind.Sequential)] public struct MpvRenderParam { public int Type; public nint Data; }
[StructLayout(LayoutKind.Sequential)] public struct MpvEvent { public int EventId; public int Error; public ulong ReplyUserdata; public nint Data; }

internal static unsafe partial class Native
{
    const string L = "libmpv-2.dll";
    [LibraryImport(L)] internal static partial nint mpv_create();
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_option_string(nint h, string name, string value);
    [LibraryImport(L)] internal static partial int mpv_initialize(nint h);
    [LibraryImport(L)] internal static partial int mpv_command(nint h, nint* args);
    [LibraryImport(L)] internal static partial MpvEvent* mpv_wait_event(nint h, double timeout);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint mpv_get_property_string(nint h, string name);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_property_string(nint h, string name, string value);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_get_property(nint h, string name, int format, void* data);
    [LibraryImport(L)] internal static partial void mpv_free(nint data);
    [LibraryImport(L)] internal static partial void mpv_terminate_destroy(nint h);
    [LibraryImport(L)] internal static partial int mpv_render_context_create(nint* res, nint mpv, MpvRenderParam* p);
    [LibraryImport(L)] internal static partial void mpv_render_context_set_update_callback(nint ctx, delegate* unmanaged[Cdecl]<nint, void> cb, nint cbCtx);
    [LibraryImport(L)] internal static partial ulong mpv_render_context_update(nint ctx);
    [LibraryImport(L)] internal static partial int mpv_render_context_render(nint ctx, MpvRenderParam* p);
    [LibraryImport(L)] internal static partial void mpv_render_context_report_swap(nint ctx);
    [LibraryImport(L)] internal static partial void mpv_render_context_free(nint ctx);
}

public static class MpvConst
{
    public const int RenderApiType = 1, RenderD3D11Init = 21, RenderD3D11Fbo = 22;
    public const int EvStartFile = 6, EvEndFile = 7, EvFileLoaded = 8, EvVideoReconfig = 17, EvPlaybackRestart = 21;
    public const int FormatDouble = 5;
}

/// <summary>Owns an mpv_handle. Events are pumped on a dedicated thread and time-stamped for latency measurements.</summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    public nint Handle { get; }
    readonly Thread _events;
    readonly object _evLock = new();
    readonly List<(int Id, long Ticks)> _log = new();
    volatile bool _stop;

    public MpvPlayer(bool hwdec, IEnumerable<string>? extra = null)
    {
        Handle = Native.mpv_create();
        if (Handle == 0) throw new InvalidOperationException("mpv_create failed");
        Opt("config", "no"); Opt("idle", "yes"); Opt("keep-open", "yes"); Opt("hr-seek", "yes");
        Opt("osc", "no"); Opt("input-default-bindings", "no"); Opt("terminal", "no");
        Opt("hwdec", hwdec ? "d3d11va" : "no"); Opt("volume", "0"); Opt("vo", "libmpv");
        foreach (var kv in extra ?? [])
        {
            var i = kv.IndexOf('=');
            Opt(kv[..i], kv[(i + 1)..]);
        }
        var rc = Native.mpv_initialize(Handle);
        if (rc < 0) throw new InvalidOperationException($"mpv_initialize rc={rc}");
        _events = new Thread(Pump) { IsBackground = true, Name = "mpv-events" };
        _events.Start();
    }

    void Opt(string k, string v) { var rc = Native.mpv_set_option_string(Handle, k, v); if (rc < 0) Console.Error.WriteLine($"option {k}={v} rc={rc}"); }

    void Pump()
    {
        while (!_stop)
        {
            var ev = Native.mpv_wait_event(Handle, 0.1);
            if (ev == null || ev->EventId == 0) continue;
            lock (_evLock) { _log.Add((ev->EventId, Stopwatch.GetTimestamp())); }
            if (ev->EventId == 1) break; // SHUTDOWN
        }
    }

    public int Command(params string[] args)
    {
        var p = (nint*)NativeMemory.AllocZeroed((nuint)((args.Length + 1) * sizeof(nint)));
        try
        {
            for (int i = 0; i < args.Length; i++) p[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            return Native.mpv_command(Handle, p);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(p[i]);
            NativeMemory.Free(p);
        }
    }

    public string? GetString(string name)
    {
        var p = Native.mpv_get_property_string(Handle, name);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); } finally { Native.mpv_free(p); }
    }
    public double GetDouble(string name) { double d = double.NaN; return Native.mpv_get_property(Handle, name, MpvConst.FormatDouble, &d) < 0 ? double.NaN : d; }
    public void Set(string name, string value) => Native.mpv_set_property_string(Handle, name, value);

    /// <summary>Timestamp mark: events with ticks &gt; mark are "after".</summary>
    public long Mark() => Stopwatch.GetTimestamp();

    /// <summary>Blocks until an event with <paramref name="id"/> arrives after <paramref name="after"/>; returns its Stopwatch ticks or -1.</summary>
    public long WaitEvent(int id, long after, int timeoutMs, CancellationToken ct = default)
    {
        var end = Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < end && !ct.IsCancellationRequested)
        {
            lock (_evLock)
                foreach (var e in _log) if (e.Id == id && e.Ticks > after) return e.Ticks;
            Thread.Sleep(1);
        }
        return -1;
    }

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        _stop = true;
        _events.Join(1000);
        Native.mpv_terminate_destroy(Handle);
    }
}
