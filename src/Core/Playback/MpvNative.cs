// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;

namespace SteamPoweredPlayer.Core.Playback;

/// <summary>mpv_render_param: a type tag and a pointer to its payload.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public int Type;
    public nint Data;
}

/// <summary>mpv_event. <see cref="Data"/> points to an event-specific struct that is valid until the next <c>mpv_wait_event</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventRaw
{
    public int EventId;
    public int Error;
    public ulong ReplyUserdata;
    public nint Data;
}

/// <summary>mpv_event_property.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public nint Name;      // char*
    public int Format;
    public nint Data;      // format-specific
}

/// <summary>mpv_event_end_file (the fields up to <c>error</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public int Reason;
    public int Error;
}

/// <summary>mpv_event_log_message.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public nint Prefix;    // char*
    public nint Level;     // char*
    public nint Text;      // char*
    public int LogLevel;
}

internal static class MpvConst
{
    // mpv_event_id
    public const int EvNone = 0, EvShutdown = 1, EvLogMessage = 2, EvCommandReply = 5, EvStartFile = 6, EvEndFile = 7,
        EvFileLoaded = 8, EvPlaybackRestart = 21, EvPropertyChange = 22;

    // mpv_format
    public const int FormatNone = 0, FormatString = 1, FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;

    // mpv_end_file_reason
    public const int EndEof = 0, EndStop = 2, EndQuit = 3, EndError = 4, EndRedirect = 5;

    // mpv_render_param_type (render.h; the D3D11 ones are the values mpv >= 0.42 uses, validated by spike S1)
    public const int RenderApiType = 1, RenderBlockForTargetTime = 12, RenderD3D11Init = 21, RenderD3D11Fbo = 22;

    // mpv_render_update_flag
    public const ulong RenderUpdateFrame = 1;
}

/// <summary>
/// Where libmpv comes from. The search order for <c>libmpv-2.dll</c>: the application directory, then
/// <see cref="SearchDirectory"/>, then the directory in the <c>SPP_LIBMPV_DIR</c> environment variable, then Windows' own search.
/// </summary>
public static class MpvLibrary
{
    public const string LibraryName = "libmpv-2";
    public const string EnvironmentVariable = "SPP_LIBMPV_DIR";

    /// <summary>An extra directory to look in (after the application directory). Set it before the first mpv call.</summary>
    public static string? SearchDirectory { get; set; }

    /// <summary>The full path of the <c>libmpv-2.dll</c> that would be loaded, or <c>null</c> when none of the directories has one.</summary>
    public static string? FindPath()
    {
        foreach (var dir in Directories())
        {
            var path = Path.Combine(dir, LibraryName + ".dll");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>True when libmpv can be bound. A successful probe loads the library, so it stays loaded for the process.</summary>
    public static bool IsAvailable() => MpvNative.TryLoad();

    private static IEnumerable<string> Directories()
    {
        yield return AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(SearchDirectory)) yield return SearchDirectory;
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
    }

    internal static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName) return IntPtr.Zero;
        var path = FindPath();
        return path is not null && NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;   // else: default probing
    }
}

/// <summary>The libmpv C API (client.h and render.h). Internal: <see cref="MpvPlayer"/> and <see cref="MpvD3D11Renderer"/> wrap it.</summary>
internal static unsafe partial class MpvNative
{
    private const string L = MpvLibrary.LibraryName;

    static MpvNative()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, MpvLibrary.Resolve); }
        catch (InvalidOperationException) { /* a resolver is already set for this assembly: keep it */ }
    }

    /// <summary>Forces the resolver to run (static constructor) and reports whether the library binds.</summary>
    internal static bool TryLoad()
    {
        try { _ = mpv_client_api_version(); return true; }
        catch (DllNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [LibraryImport(L)] internal static partial ulong mpv_client_api_version();
    [LibraryImport(L)] internal static partial nint mpv_create();
    [LibraryImport(L)] internal static partial int mpv_initialize(nint h);
    [LibraryImport(L)] internal static partial void mpv_terminate_destroy(nint h);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_option_string(nint h, string name, string value);
    [LibraryImport(L)] internal static partial int mpv_command(nint h, nint* args);
    [LibraryImport(L)] internal static partial int mpv_command_async(nint h, ulong replyUserdata, nint* args);
    [LibraryImport(L)] internal static partial MpvEventRaw* mpv_wait_event(nint h, double timeout);
    [LibraryImport(L)] internal static partial void mpv_set_wakeup_callback(nint h, delegate* unmanaged[Cdecl]<nint, void> cb, nint ctx);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_request_log_messages(nint h, string minLevel);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint mpv_get_property_string(nint h, string name);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_property_string(nint h, string name, string value);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_get_property(nint h, string name, int format, void* data);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_property(nint h, string name, int format, void* data);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_observe_property(nint h, ulong replyUserdata, string name, int format);
    [LibraryImport(L)] internal static partial void mpv_free(nint data);
    [LibraryImport(L)] internal static partial nint mpv_error_string(int error);

    [LibraryImport(L)] internal static partial int mpv_render_context_create(nint* res, nint mpv, MpvRenderParam* p);
    [LibraryImport(L)] internal static partial void mpv_render_context_set_update_callback(nint ctx, delegate* unmanaged[Cdecl]<nint, void> cb, nint cbCtx);
    [LibraryImport(L)] internal static partial ulong mpv_render_context_update(nint ctx);
    [LibraryImport(L)] internal static partial int mpv_render_context_render(nint ctx, MpvRenderParam* p);
    [LibraryImport(L)] internal static partial void mpv_render_context_report_swap(nint ctx);
    [LibraryImport(L)] internal static partial void mpv_render_context_free(nint ctx);

    internal static string ErrorString(int error)
    {
        var p = mpv_error_string(error);
        return (p == 0 ? null : Marshal.PtrToStringUTF8(p)) ?? $"error {error}";
    }
}
