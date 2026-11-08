// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Net;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>The export part of <c>--selftest</c>: every preset, its colour range, and a 300 ms sync error found and fixed.</summary>
public static class ExportSelfChecks
{
    private static readonly string[] PresetPassColourRanges = ["tv", "unknown", ""];

    /// <summary>
    /// Encodes 6 s of a synthetic session with every preset. Limited-range source must stay limited: a full-range flag with limited
    /// values (or the reverse) washes the picture out or crushes it, and it differs per encoder.
    /// </summary>
    public static async Task<CheckResult> PresetsAsync(SessionMedia media, string scratch, CancellationToken ct)
    {
        await Encoders.DetectAsync().ConfigureAwait(false);
        var start = media.ChunkStartSeconds(media.FirstChunk!.Value) + 3;
        var failed = new List<string>();
        var ranges = new List<string>();
        var choices = AllChoices();
        foreach (var preset in choices)
        {
            var settings = preset.Build();
            if (settings.TargetSize) settings.TargetMB = 1;
            var folder = Path.Combine(scratch, "preset-" + preset.Id);
            try
            {
                var r = await ExportService.RunAsync(new ExportJob(media, [new ExportSpan(start, start + 6)], settings, new EditSettings(), folder, preset.Id), null, ct).ConfigureAwait(false);
                var info = await Ff.ProbeAsync(r.Path, ct).ConfigureAwait(false);
                if (info is null || info.Duration < 5.5 || info.Duration > 6.5 || !info.HasAudio) failed.Add($"{preset.Id}: duration {info?.Duration.ToString("0.00", CultureInfo.InvariantCulture) ?? "unreadable"}");
                if (settings.TargetSize && r.Bytes > 1024 * 1024) failed.Add($"{preset.Id}: over the target size");
                var range = await ColourRangeAsync(r.Path, ct).ConfigureAwait(false);
                ranges.Add($"{settings.Encoder} {(range.Length == 0 ? "unknown" : range)}");
                if (!PresetPassColourRanges.Contains(range)) failed.Add($"{preset.Id}: colour range {range}");
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                failed.Add($"{preset.Id}: {Redactor.Describe(e)}");
            }
        }
        return CheckResult.From(failed.Count == 0, "export.presets", "Every export preset encodes (range, audio, target size, colour range)",
            failed.Count == 0 ? $"{choices.Count} choices; " + string.Join(", ", ranges.Distinct()) : string.Join("; ", failed));
    }

    private sealed record Choice(string Id, Func<EncodeSettings> Build);

    /// <summary>Every ladder tier on each engine this machine has, every special preset, and FastEncoder's other presets.</summary>
    private static List<Choice> AllChoices()
    {
        var choices = new List<Choice>();
        foreach (var tier in ExportLadder.Tiers)
        {
            choices.Add(new(tier.Id + "-cpu", () => ExportLadder.Build(tier, LadderEngine.Cpu, Encoders.BestGpu)));
            if (ExportLadder.UsesGpu(tier, LadderEngine.Gpu, Encoders.BestGpu))
                choices.Add(new(tier.Id + "-gpu", () => ExportLadder.Build(tier, LadderEngine.Gpu, Encoders.BestGpu)));
        }
        choices.AddRange(Presets.All.Select(p => new Choice(p.Id, p.Build)));
        return choices;
    }

    /// <summary>A session whose clicks land 300 ms after its flashes must be measured, fixed by an export, and measure as in sync afterwards.</summary>
    public static async Task<CheckResult> SyncAsync(string scratch, CancellationToken ct)
    {
        const string id = "export.sync", name = "Audio-sync detect and fix (300 ms injected)";
        Directory.CreateDirectory(scratch);
        const double late = 0.3;
        var inv = CultureInfo.InvariantCulture;
        var clicks = string.Create(inv, $"lt(mod((t-{late})*1.3,1),0.013)+lt(mod((t-{late})*0.77+0.2,1),0.008)");
        var psi = Ff.StartInfo(Ff.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=c=black:s=320x180:r=60:d=18",
            "-f", "lavfi", "-i", $"aevalsrc='0.8*min(1,{clicks})*sin(2*PI*2500*t)+0.01*(random(0)-0.5)':s=48000:d=18",
            "-vf", "geq=lum='16+219*mod(floor(T*1.3)+floor(T*0.77+0.2),2)':cb=128:cr=128",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "180", "-keyint_min", "180", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-shortest",
            "-f", "dash", "-seg_duration", "3", "-use_template", "1", "-use_timeline", "0",
            "-init_seg_name", "init-stream$RepresentationID$.m4s",
            "-media_seg_name", "chunk-stream$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", "id=0,streams=v id=1,streams=a",
            "session.mpd",
        ]);
        psi.WorkingDirectory = scratch;                                 // the dash muxer writes its segments relative to here
        using (var gen = System.Diagnostics.Process.Start(psi)!)
        {
            var drainOut = gen.StandardOutput.ReadToEndAsync(ct);
            var errors = await gen.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await drainOut.ConfigureAwait(false);
            await gen.WaitForExitAsync(ct).ConfigureAwait(false);
            if (gen.ExitCode != 0) return CheckResult.Fail(id, name, "could not generate the source: " + errors);
        }

        var media = new SessionMedia(scratch, [new SegmentRun(1, 6)], 3, false, false);
        var span = new ExportSpan(0, 18);
        var advice = SyncAdvice.From(await ExportService.DetectSyncAsync(media, span, null, ct).ConfigureAwait(false));
        if (advice.Verdict != SyncVerdict.Fix || Math.Abs(advice.AudioDelay + late) > 0.04)
            return CheckResult.Fail(id, name, $"measured {advice.Verdict}, delay {advice.AudioDelay.ToString("0.000", inv)} s");

        var fixedFile = await ExportService.RunAsync(new ExportJob(media, [span], new EncodeSettings { Encoder = "x264", Quality = 18, Speed = 0, Audio = "aac", AudioKbps = 128 },
            new EditSettings(), Path.Combine(scratch, "out"), "fixed", AudioDelay: advice.AudioDelay), null, ct).ConfigureAwait(false);
        var info = await Ff.ProbeAsync(fixedFile.Path, ct).ConfigureAwait(false);
        var after = await AudioSync.DetectAsync(fixedFile.Path, 0, info?.Duration ?? 18, null, ct).ConfigureAwait(false);
        return CheckResult.From(after.Confidence == AudioSync.Confidence.High && AudioSync.InSync(after.AudioLate), id, name,
            $"measured {(-advice.AudioDelay).ToString("+0.000;-0.000", inv)} s; after the fix {after.AudioLate.ToString("+0.000;-0.000", inv)} s ({after.Confidence})");
    }

    /// <summary>Every third file's connection dies halfway through; the download must finish and match the source byte for byte.</summary>
    public static async Task<CheckResult> InterruptedDownloadAsync(SessionMedia media, string scratch, CancellationToken ct)
    {
        const string id = "net.download", name = "Interrupted download resumes without corruption";
        var chunks = media.Chunks().Take(6).ToList();
        var cacheDir = Path.Combine(scratch, "cache");
        var files = new DyingFiles();
        var cache = await LocalChunkCache.OpenAsync(cacheDir, 1L << 30, ct).ConfigureAwait(false);
        var result = await new ChunkDownloader(cache, files, parallel: 1).RunAsync(new DownloadRequest("net-selftest", "clip", media, chunks), null, ct).ConfigureAwait(false);
        var local = Path.Combine(cacheDir, "net-selftest", "clip");
        var names = chunks.SelectMany(c => new[] { FolderNames.ChunkFile(0, c), FolderNames.ChunkFile(1, c) }).Concat([FolderNames.InitFile(0), FolderNames.InitFile(1)]);
        var different = names.Count(n => !File.Exists(Path.Combine(local, n))
            || !File.ReadAllBytes(Path.Combine(local, n)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(media.Folder, n))));
        return CheckResult.From(result.State == DownloadState.Done && different == 0 && files.Killed > 0, id, name,
            $"{files.Killed} connections killed, {result.Done}/{result.Total} files, {different} different");
    }

    private sealed class DyingFiles : IRemoteFiles
    {
        private readonly SharedFolderFiles _real = new();
        private int _opens;
        public int Killed { get; private set; }

        public ValueTask<long?> SizeAsync(string path, CancellationToken ct) => _real.SizeAsync(path, ct);

        public Stream Open(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (++_opens % 3 != 0) return new MemoryStream(bytes);
            Killed++;
            return new ConcatFailure(bytes[..(bytes.Length / 2)]);
        }
    }

    /// <summary>Delivers its bytes, then fails like a dropped connection instead of ending.</summary>
    private sealed class ConcatFailure(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Read(buffer.Span);
            return n > 0 ? new(n) : throw new IOException("connection reset");
        }
    }

    private static async Task<string> ColourRangeAsync(string path, CancellationToken ct)
    {
        var r = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_range", "-of", "csv=p=0", path], ct).ConfigureAwait(false);
        return r.StdOut.Trim();
    }
}
