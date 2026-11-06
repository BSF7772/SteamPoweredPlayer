<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Spike S6: `fastencoder-link`

**Question.** Can SteamPoweredPlayer compile FastEncoder's `src/Core` in by link (plan D5), with zero changes to FastEncoder, and
does `Pipeline.EncodeAsync` with the **GPU Turbo** preset and `AudioDelay = 0.3` turn a 30 s lossless range file from a real Steam
session into a playable, correctly shifted output?

## What this is
A throwaway console harness (`net10.0-windows`, x64): `FastEncoderLink.csproj` + `Program.cs`.

1. `Ff.Locate()` and `Encoders.DetectAsync()` (so "GPU Turbo" resolves to a real GPU encoder).
2. **Stage 1, lossless range** (plan §3 export path): byte-concatenate `init-stream{0,1}.m4s` + 10 consecutive chunks (30 s) for video
   and audio (files opened read + write + delete sharing, read only), then `ffmpeg -c copy -avoid_negative_ts make_zero -movflags +faststart`.
3. **Stage 2:** `Pipeline.EncodeAsync(new EncodeRequest(info, 0, null, Presets.Get("turbo")!.Build(), new EditSettings(), out, AudioDelay: ...))`.
4. Measure, per case: probe of input/output, a full decode (`ffmpeg -xerror`), `AudioSync.DetectAsync` on input and output, and an
   independent waveform cross-correlation (±1.5 s, 1 ms steps) of input audio against output audio, which gives the actual shift of the soundtrack.

Cases:

| Case | Input | `AudioDelay` | Expected shift of the sound |
|---|---|---|---|
| `real_delay+300` | the untouched 30 s real range | +0.3 s | +300 ms (later) |
| `real_late300_fix` | the range with sound injected **+300 ms late** (`-itsoffset`) | −0.3 s | −300 ms (back to original) |
| `real_early300_fix` | the range with sound injected 300 ms early (`-ss`) | +0.3 s | +300 ms (back to original) |
| `synthetic_late300_fix` | FastEncoder's own synthetic flash + click clip, sound 300 ms late | −0.3 s | −300 ms |
| `synthetic_delay+300` | the same synthetic clip | +0.3 s | +300 ms |

## Reproduce
Needs .NET SDK 10, ffmpeg + ffprobe ≥ 8 on `PATH` (tested with 9.0), an NVIDIA GPU with NVENC for the GPU Turbo path.

```powershell
# FastEncoder: defaults to the planned submodule location <repo>\external\FastEncoder\ ; any checkout works
dotnet build spikes/006-fastencoder-link/FastEncoderLink.csproj -c Release -p:FastEncoderDir=<FastEncoderDir> -o <BuildDir>

# <SessionDir> = <RecordingRoot>\video\bg_<gameid>_<yyyyMMdd>_<HHmmss> (a finished session; only read)
# <FirstChunk> <ChunkCount>  = e.g. 400 10  (10 chunks x 3 s = 30 s)
<BuildDir>\FastEncoderLink.exe <SessionDir> <FirstChunk> <ChunkCount> spikes/006-fastencoder-link/out/run
```
`out/` is gitignored (the harness writes ~700 MB of media there, nothing is deleted by it; clean up by hand).
Exit code 0 = all cases passed. The log is `out/run/report.txt`.

The run recorded below used: a finished HEVC 2560x1440 60 fps session, chunks 400-409, FastEncoder commit `e3c1b04`, ffmpeg 9.0 (gyan.dev
full build, from `PATH`; not downloaded by this spike, so no SHA-256 is pinned here), .NET SDK 10.0.401.
Hardware class: desktop NVIDIA GPU (NVENC AV1/HEVC/H.264), NVMe SSD.

## Linking
`FastEncoderLink.csproj` uses `<Compile Include="$(FastEncoderDir)src\Core\X.cs" Link="FastEncoder\X.cs" />`. **Zero changes** to
FastEncoder: `git status` in the checkout is clean after building and running. Build: 0 warnings, 0 errors, 3 s.

**Minimal set: `Ff.cs`, `Encoders.cs`, `Presets.cs`, `Pipeline.cs`, `AudioSync.cs`** (namespace `FastEncoder.Core`).

| File | Depends on | Notes |
|---|---|---|
| `Ff.cs` | BCL only | ffmpeg/ffprobe lookup, `RunAsync`, `ProbeAsync` (`MediaInfo`), `Sec` |
| `Encoders.cs` | `Ff` | encoder catalogue, `DetectAsync` (proves each GPU encoder with a 0.2 s test encode) |
| `Presets.cs` | `Encoders` | `Presets.Get("turbo")` etc. |
| `Pipeline.cs` | `Ff`, `Encoders`, `Presets` | `Build`, `EncodeAsync`, estimate |
| `AudioSync.cs` | `Ff` only | independent of `Pipeline` (can be linked alone) |

Not linked and **not needed**: `AppSettings.cs` (JSON settings, no UI, FastEncoder-app specific), `SelfTest.cs` (FastEncoder's own
`--selftest`), `Taskbar.cs` (COM/user32 taskbar flash). **No UI / WinUI dependency blocks linking**: the five files use only the BCL.
The harness needs `net10.0-windows` only because the plan's real app does; the linked code itself would compile for plain `net10.0`.

## Results (raw)
Source: `out/final2/report.txt` of this run. Sign convention: FastEncoder's `AudioDelay` is "seconds to delay the soundtrack
(negative = earlier)"; `AudioSync.Result.AudioLate` is "seconds the sound arrives after the picture; fix = delay by −AudioLate".

Lossless range (stage 1): **10 chunks / 30 s: byte-concat 74 ms, concat + remux 390 ms** (warm cache; an earlier cold run: 552 ms),
92 631 328 B (22 Mbps HEVC + AAC).

| Case | Out duration | Out size | Encode wall | Speed | Decodes (`-xerror`) | Waveform shift in→out | Expected |
|---|---|---|---|---|---|---|---|
| `real_delay+300` | 30.016 s (in 30.016) | 48 496 680 B (52.4 %) | 14.06 s | 2.1× | exit 0 | **+300 ms** (corr 1.01) | +300 |
| `real_late300_fix` | 30.016 s (in 30.316) | 48 500 019 B (52.4 %) | 13.97 s | 2.1× | exit 0 | **−300 ms** (corr 1.01) | −300 |
| `real_early300_fix` | 30.000 s (in 30.000) | 48 495 724 B (52.4 %) | 15.40 s | 1.9× | exit 0 | **+300 ms** (corr 1.01) | +300 |
| `synthetic_late300_fix` | 30.000 s | 406 199 B (46.7 %) | 4.05 s | 7.4× | exit 0 | **−300 ms** (corr 0.99) | −300 |
| `synthetic_delay+300` | 30.000 s | 406 719 B (46.7 %) | 3.85 s | 7.8× | exit 0 | **+300 ms** (corr 1.00) | +300 |

Output format of every GPU Turbo run: AV1 (`av1_nvenc`, p7, `-tune hq -multipass fullres`, CQ 36) 10-bit `yuv420p10le`, **`color_range=pc`
(full range survives)**, CFR 60 fps, Opus 96 kbps, MP4 `+faststart`, streams start at 0.000. `Encoders.DetectAsync` found
`svtav1, av1_nvenc, x265, hevc_nvenc, x264, h264_nvenc`. mpv (headless, `hwdec=no`) also opens and decodes the output (AV1 + Opus, no warnings).

`AudioSync` (FastEncoder's detector), `AudioLate` in ms:

| Clip | Input | Output | Delta | Confidence |
|---|---|---|---|---|
| synthetic, sound 300 ms late, fix −0.3 | +277 | **−23** (in sync: `InSync` window −45…+80) | −300 | High 12/12 |
| synthetic, `AudioDelay` +0.3 | +277 | +577 | +300 | High 12/12 |
| real range, `AudioDelay` +0.3 | −997 | +781 | +1778 | **Low** 2/6 and 4/9 |
| real range, +300 ms injected, fix −0.3 | +786 | −999 | −1785 | **Low** 3/8 and 2/6 |
| real range, −300 ms injected, fix +0.3 | 0 | −1002 | −1002 | **Low** 1/1 and 2/4 |

## Findings
1. **Linking works, unchanged.** Five files, BCL only, 0 warnings, FastEncoder checkout untouched.
2. **`Pipeline.EncodeAsync` applies the delay exactly**: the independent waveform correlation measures the shift to the millisecond in all
   five cases (±0 ms at 1 ms resolution; budget ±10 ms), and the AV1/Opus output is 30 s, decodes cleanly, plays in mpv, keeps full-range
   colour. On the synthetic clip FastEncoder's own detector confirms the result: +277 ms before, −23 ms after (inside its "in sync" window).
   The 23 ms residual is the detector's own bias (it reads 277, not 300, on the unmodified input), not a pipeline error.
3. **`AudioSync` cannot verify sync on arbitrary real gameplay.** A real 30 s window without sharp picture/sound coincidences gives
   `Confidence.Low` with offsets jumping by ±1 s. This is the detector's documented design (it needs onset pairs and rejects coincidences),
   not a link problem. It is why the harness adds the waveform cross-correlation and the synthetic clip. Detect needs a longer or
   event-rich window (default analyses up to 75 s from the middle). The app must show "Low confidence: not applied" rather than a number.
4. **Speed.** GPU Turbo is 2.1× realtime for 1440p60 (14 s for 30 s) because the preset is `p7` + `multipass fullres` + lookahead. On the
   synthetic 720p it is 7.5×. Not a link issue, but "Turbo" is slower than the name suggests on 1440p60 content; see needs_owner.
5. **Lossless 30 s range: 390 ms warm (552 ms cold), inside the plan's < 1 s budget.**

### API friction (nothing blocks reuse; all handled in the harness)
- **Order dependency:** `Presets.Get("turbo")!.Build()` before `Encoders.DetectAsync()` silently returns the CPU `svtav1` settings
  (`Encoders.Available` starts as CPU-only). Call `Ff.Locate()` then `await Encoders.DetectAsync()` first. Build() reads global state.
- **Global static state:** `Ff.Ffmpeg/Ffprobe`, `Encoders.Available/HasOpus` and `Ff.TempRoot` (`%TEMP%\FastEncoder`, created by
  `Locate`) are process-wide. Fine for one app, awkward for parallel tests.
- **ffmpeg location is not configurable:** `Ff.Locate()` only looks next to the exe and on `PATH` (no setter, no env var). With the
  portable toolchain in `.tools/` the app must put it on `PATH` (or copy it beside the exe) before calling `Locate`.
- **Input must be a file path** (`MediaInfo.Path`, built by `Ff.ProbeAsync`): no stdin/pipe input. Compatible with plan D4 (stage 1
  writes a temp range file anyway), but that file must live until the encode ends.
- `AudioDelay` semantics are opposite in sign to `AudioSync.Result.AudioLate` (fix = `−AudioLate`); the app needs one tested helper for it.

Verdict: VALIDATED
