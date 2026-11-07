// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>
/// Headless playback for diagnostics: one real libmpv (<c>vo=null</c>, <c>ao=null</c>) plus a <see cref="PlaybackSession"/> on it.
/// The render path (D3D11 swap chain, XAML) is not part of it. <see cref="HeadlessGpuHwdec"/> is <c>d3d11va-copy</c>: plain
/// <c>d3d11va</c> needs the renderer's device and silently decodes in software without a video output.
/// </summary>
public sealed class PlaybackRig : IAsyncDisposable
{
    /// <summary>mpv's hardware decode that works without a video output (frames are copied back to system memory).</summary>
    public const string HeadlessGpuHwdec = "d3d11va-copy";

    private readonly HiddenWindow? _window;

    private PlaybackRig(MpvPlayer player, PlaybackSession session, DecodeMode mode, HiddenWindow? window)
    {
        Player = player;
        Session = session;
        Mode = mode;
        _window = window;
    }

    public MpvPlayer Player { get; }
    public PlaybackSession Session { get; }
    public DecodeMode Mode { get; }

    /// <summary>Wid of the hidden render window (null for <c>vo=null</c>); the mpv CLI reference renders into the same kind of window.</summary>
    public string? Wid => _window?.Wid;

    /// <summary>The mpv options that make hardware decode zero-copy into mpv's D3D11 renderer on a hidden window.</summary>
    public static IReadOnlyDictionary<string, string> RenderOptions(string wid) => new Dictionary<string, string>
    {
        ["vo"] = "gpu",
        ["gpu-api"] = "d3d11",
        ["gpu-context"] = "d3d11",
        ["wid"] = wid,
        ["hwdec"] = "d3d11va",
    };

    /// <summary>mpv's <c>hwdec-current</c>: <c>no</c> when software decode is in use (also before anything is loaded).</summary>
    public string HwdecCurrent => Player.GetString("hwdec-current") is { Length: > 0 } s ? s : "no";

    /// <summary>True when mpv really decodes on the GPU right now.</summary>
    public bool UsesHardware => !string.Equals(HwdecCurrent, "no", StringComparison.OrdinalIgnoreCase);

    /// <param name="renderHidden">
    /// With <see cref="DecodeMode.Gpu"/>: render into a hidden window (<c>vo=gpu</c>, <c>hwdec=d3d11va</c>, zero-copy) instead of
    /// <c>vo=null</c> with <c>d3d11va-copy</c>. Falls back to the copy-back path when no window can be created. Ignored for CPU decode.
    /// </param>
    public static PlaybackRig Create(DecodeMode mode, int? windowCount = null, MpvPlayerOptions? options = null, bool renderHidden = false)
    {
        DiagnosticsEnvironment.PrepareMpv();
        var o = (options ?? new MpvPlayerOptions()) with
        {
            Output = MpvOutput.Headless,
            Decode = mode,
            InitialVolume = 0,
            LogLevel = "no",
        };
        var window = renderHidden ? HiddenWindow.TryCreate() : null;
        var extra = new Dictionary<string, string>(o.Extra ?? new Dictionary<string, string>());
        if (window is not null)
        {
            foreach (var (k, v) in RenderOptions(window.Wid)) extra[k] = v;
            if (mode == DecodeMode.Cpu) extra["hwdec"] = "no";
        }
        else if (mode == DecodeMode.Gpu) extra["hwdec"] = HeadlessGpuHwdec;
        o = o with { Extra = extra };
        try
        {
            var player = new MpvPlayer(o);
            var session = windowCount is { } c ? new PlaybackSession(player, null, c) : new PlaybackSession(player);
            return new(player, session, mode, window);
        }
        catch
        {
            window?.Dispose();
            throw;
        }
    }

    /// <summary>Loads the tiny pre-warm EDL (plan §3 item 0) so the next open does not pay mpv's first-load cost.</summary>
    public Task<bool> PreWarmAsync(SessionMedia media, CancellationToken ct = default)
    {
        var (edl, start) = PlaybackSession.BuildPreWarm(media);
        return Player.PreWarmAsync(edl, start, ct);
    }

    /// <summary>mpv's <c>time-pos</c> (media seconds), NaN when unavailable.</summary>
    public double TimePos => Player.GetDouble("time-pos");

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().ConfigureAwait(false);
        await Player.DisposeAsync().ConfigureAwait(false);
        _window?.Dispose();
    }
}

/// <summary>Finds what the diagnostics need from the environment (libmpv in a dev tree, ffmpeg).</summary>
public static class DiagnosticsEnvironment
{
    /// <summary>
    /// Makes libmpv findable when the exe has none next to it: <c>SPP_LIBMPV_DIR</c> is honoured by <see cref="MpvLibrary"/>;
    /// otherwise a <c>.tools/libmpv/&lt;version&gt;</c> or <c>.tools/s1/mpv-dev</c> directory in a parent of the program is used.
    /// </summary>
    public static void PrepareMpv()
    {
        if (MpvLibrary.FindPath() is not null || MpvLibrary.IsAvailable()) return;
        foreach (var root in ParentDirectories())
        {
            var tools = Path.Combine(root, ".tools");
            if (!Directory.Exists(tools)) continue;
            var candidates = new List<string>();
            var lib = Path.Combine(tools, "libmpv");
            if (Directory.Exists(lib)) candidates.AddRange(Directory.EnumerateDirectories(lib).OrderDescending());
            candidates.Add(Path.Combine(tools, "s1", "mpv-dev"));
            foreach (var c in candidates)
            {
                if (!File.Exists(Path.Combine(c, "libmpv-2.dll"))) continue;
                MpvLibrary.SearchDirectory = c;
                if (MpvLibrary.IsAvailable()) return;
            }
        }
    }

    /// <summary>The mpv command line player of a dev tree (<c>.tools/mpv/&lt;version&gt;/mpv.exe</c>), or null.</summary>
    public static string? FindMpvCli()
    {
        foreach (var root in ParentDirectories())
        {
            var dir = Path.Combine(root, ".tools", "mpv");
            if (!Directory.Exists(dir)) continue;
            foreach (var v in Directory.EnumerateDirectories(dir).OrderDescending())
            {
                var exe = Path.Combine(v, "mpv.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    private static IEnumerable<string> ParentDirectories()
    {
        // The worktree's own .tools first, then the parents (a worktree lives below the main checkout).
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent) yield return d.FullName;
    }
}
