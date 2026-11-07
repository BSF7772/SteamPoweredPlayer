// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SteamPoweredPlayer.Core;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// A synthetic recording for <c>--sample</c>, so the Player page is demoable without Steam: a 120 s H.264 + AAC DASH session in Steam's
/// file naming (3 s chunks, one IDR per chunk, generated once with ffmpeg into the temp folder), its trickplay (the real generator,
/// CPU decode) and made-up timeline entries. Nothing comes from a real library.
/// </summary>
public sealed class SampleSession
{
    public const int Chunks = 40;
    public const string TrickKey = "sample-session";

    public required string Root { get; init; }
    public required string Folder { get; init; }
    public required SessionMedia Media { get; init; }
    public required IReadOnlyList<Marker> Markers { get; init; }
    public TrickplayIndex? Trickplay { get; init; }

    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "SteamPoweredPlayer-sample");

    /// <summary>Null when ffmpeg is missing or fails (the sample library then has no playable media).</summary>
    public static async Task<SampleSession?> CreateAsync(CancellationToken ct = default)
    {
        var root = DefaultRoot;
        var folder = Path.Combine(root, "session");
        try
        {
            if (!IsComplete(folder) && !await GenerateDashAsync(folder, ct).ConfigureAwait(false)) return null;

            var media = new SessionMedia(folder, [new SegmentRun(1, Chunks)], SessionMedia.DefaultSegmentSeconds, Live: false, Temporary: false);
            // The data folder must not overlap the (made-up) recording path: two siblings under the sample root.
            var data = DataFolder.Open(Path.Combine(root, "data"), Path.Combine(root, "recordings"));
            if (!File.Exists(Path.Combine(data.TrickDir, TrickKey, TrickplayManifest.FileName)) && Core.Library.Trickplay.FfmpegAvailable())
                await Core.Library.Trickplay.GenerateAsync(data, TrickKey, media, new TrickplayOptions(Mode: Core.DecodeMode.Cpu), null, ct).ConfigureAwait(false);

            return new SampleSession
            {
                Root = root, Folder = folder, Media = media, Markers = BuildMarkers(),
                Trickplay = TrickplayIndex.TryLoad(data, TrickKey),
            };
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsComplete(string folder) =>
        File.Exists(Path.Combine(folder, FolderNames.ChunkFile(0, Chunks))) && File.Exists(Path.Combine(folder, FolderNames.ChunkFile(1, Chunks)))
        && File.Exists(Path.Combine(folder, FolderNames.InitFile(0))) && File.Exists(Path.Combine(folder, FolderNames.InitFile(1)));

    private static async Task<bool> GenerateDashAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = folder };
        foreach (var a in new[]
        {
            "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=960x540:rate=30", "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000",
            "-t", (Chunks * 3).ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-g", "90", "-keyint_min", "90",
            "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k", "-f", "dash", "-seg_duration", "3", "-use_template", "1",
            "-use_timeline", "0", "-init_seg_name", "init-stream$RepresentationID$.m4s", "-media_seg_name", "chunk-stream$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", "id=0,streams=v id=1,streams=a", "session.mpd",
        }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        _ = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode == 0 && IsComplete(folder);
    }

    private static List<Marker> BuildMarkers()
    {
        static Marker M(double start, string icon, string title, TimelineEntryType type = TimelineEntryType.Event, double duration = 0) =>
            new(new TimelineEntry(null, (long)(start * 1000), type, icon, title, null, icon, null, (long)(duration * 1000), false, null, [], null, null),
                start, duration > 0 ? start + duration : null, MarkerAlignment.Exact);
        return
        [
            M(0, "phase", "Round 1", TimelineEntryType.Phase, 62),
            M(9, "cs2_gun_kill", "Eliminated Rival 1"),
            M(22, "cs2_death", "Eliminated by Rival 2"),
            M(38, "cs2_bomb_plant", "Bomb planted"),
            M(52, "cs2_multi_kill", "Double kill", duration: 8),
            M(67, "steam_screenshot", "Screenshot", TimelineEntryType.Screenshot),
            M(81, "cs2_knife_kill", "Knife kill"),
            M(96, "cs2_gun_kill", "Eliminated Rival 3"),
            M(108, "steam_achievement", "Achievement unlocked", TimelineEntryType.Achievement),
        ];
    }
}
