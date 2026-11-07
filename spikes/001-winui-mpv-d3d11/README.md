<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Spike S1: WinUI 3 + libmpv (D3D11 render API) in a SwapChainPanel

Question: can an unpackaged, self-contained WinUI 3 / .NET 10 app embed libmpv through the **D3D11 render API**
(`mpv_render_context`) into a `SwapChainPanel`, play the S2 lead EDL, keep a XAML overlay on top, survive resize and fullscreen,
and still meet the plan's S1 budgets?

**Status: part A (build + correctness) done. Part B (timed) run once on an idle machine, with a shortened 5-minute soak; results below.**

## What is here

| path | purpose |
|---|---|
| `S1App/` | the spike app (≈ 900 lines; seed for Phase 2 `src/Core/Playback` + the player view) |
| `S1App/Mpv.cs` | libmpv `LibraryImport` binding: create/init, options, commands, events, render context (D3D11) |
| `S1App/Renderer.cs` | D3D11 device + DXGI composition swap chain (FlipSequential, 3 buffers) bound to the panel; render thread; present stats; black-frame probe; GPU memory query |
| `S1App/MainWindow.xaml(.cs)` | window, panel, XAML overlay (buttons + text), pre-warm, seek-by-reload, fullscreen via `AppWindow` presenter |
| `S1App/SelfTest.cs` | part A automation (`--mode selftest`), writes `<tag>.selftest.txt/.json` + screenshots |
| `S1App/PerfRun.cs` | part B measurement run (`--mode play`): CPU per media-second, memory/GPU memory samples, present stats, seek-by-reload latency |
| `perf/run-perf.ps1` | part B driver (see below) |

The EDL comes from S2's `Session.cs` (`spikes/002-edl-mp4dash-cli/S2Tool/`), linked into the project, so there is one builder.
S2 rules applied: seek = `loadfile <edl> replace -1 start=<t>` (never a plain seek across revisited chunks), pre-warm at startup with a tiny 2-chunk EDL.

## Environment (hardware class only)

Desktop, NVIDIA GPU (NVENC-capable), NVMe, 16 logical cores, Windows 11. .NET SDK 10.0.401. Fixture: the S2 `stale-100` session
(HEVC 2560x1440, 60 fps, 100 chunks = 300 s, lead EDL: chunks 1840..1939, T0 = 5517 s), read-only, not copied.

Pinned inputs (all under the gitignored `.tools/`, nothing system-wide):

| item | version | SHA-256 |
|---|---|---|
| `libmpv-2.dll` (from `mpv-dev.7z`, shinchiro mpv-winbuild-cmake release 20261105, same build family as the CLI below; mpv 0.42.0) | | `1d3c4067692048c6cab7735954e2299664b94c098fcdb8f7bc700e6a6a133ed6` |
| `.tools/s1/dl/mpv-dev.7z` (31 791 127 bytes) | 20261105 | `f463cc69a7a1610937688b837be9f005cf163c26f2e35e50aec8cc7bde2894b4` |
| `.tools/s1/dl/render_d3d11.h` (libmpv client header for the D3D11 API) | | `7b880453ee3c780b0d0f1c3f8ecaa1d23b289522f4c687e6857c8475f4a3f6fb` |
| `.tools/mpv/mpv-20261105/mpv.exe` (reference CLI, shared with S2) | v0.42.0-1-g3138fc8ee | `63de44712d633a59b50a6ef19f91a6f51cefa6079db59c69bbd31bac03c01060` |
| Microsoft.WindowsAppSDK | 1.8.260921001 | NuGet package hash (restore) |
| Vortice.Direct3D11 / Vortice.DXGI | 3.8.3 | NuGet |
| System.Drawing.Common (**spike only**, screen capture for the overlay check) | 10.0.0 | NuGet |

The exact download URL of `mpv-dev.7z` was not recorded during the run; it is the `mpv-dev` asset of the release named above. Re-pin by SHA-256.

## Reproduce part A

```powershell
$env:NUGET_PACKAGES = '<repo>/.tools/s1/nuget'; $env:DOTNET_CLI_HOME = '<repo>/.tools/s1/dotnet-home'   # keep everything inside .tools
dotnet build spikes/001-winui-mpv-d3d11/S1App/S1App.csproj -c Release -o spikes/001-winui-mpv-d3d11/out/bin
spikes/001-winui-mpv-d3d11/out/bin/S1App.exe --mode selftest --fixture <.tools>/fixtures-local/s2/stale-100 --out spikes/001-winui-mpv-d3d11/out --tag partA2
# exit code 0 = pass; report in out/partA2.selftest.txt, screenshots out/partA2-*.png (real footage: never commit)
# interactive: S1App.exe --mode interactive --fixture ...   (Fullscreen / Pause buttons, live stats text)
```
The window must stay on top and unobstructed during the self-test (the overlay/black-frame checks use screen capture). One session ≈ 25 s.

## Part A results (correctness): 26/26 checks PASS

Run `partA2` (an earlier run, `partA`, failed 6 checks because the test did not wait for `ResizeClient` to land and another window covered the app:
a test-harness bug, fixed; the first run's files are left in `out/`).

| check | result |
|---|---|
| D3D11 render API + hwdec | `vo=libmpv`, `hwdec-current=d3d11va`, 2560x1440 HEVC played in a 1280x720 panel, presents +60 per media-second |
| open → playback-restart | event received (time not a result) |
| XAML overlay over video | button+text rendered over the swap chain: changed inside overlay 0.959, outside 0.000 (pause + collapse-overlay diff), white text pixels 0.133 |
| resize 960x540, 1600x900, 800x450, 1280x720 | swap chain follows the panel exactly each time, presents keep flowing, 0 black frames |
| drag-resize simulation (burst of resizes) | no black frame, no device loss |
| fullscreen ×2 (UI-automation click on the XAML button, `AppWindow` FullScreen/Default) | swap chain 2560x1440 = screen, overlay still drawn, back to windowed 1280x720 each time, 0 black frames |
| seek-by-reload ×6 (`loadfile start=`) | landed 0.413..0.433 s past target (matches S2's +0.42 s), playback continues |
| final device state | `DeviceRemovedReason` 0x00000000, present errors 0, black-frame probe 0 / 1129 back buffers |

Seek latencies seen in the self-test (40–147 ms) are **informational only** (machine in use).
`wid` fallback: **not needed**, the D3D11 render API works. It was therefore not implemented.

## Part B: timed measurements

### Results (one run, `-Repeats 3 -SoakSeconds 300`)
Desktop NVIDIA GPU, 16 logical cores, HEVC 1440p60, `hwdec=d3d11va`. Machine CPU before start 6.9 % (idle gate passed, no `-Force`). Total
wall time 765 s. Medians of 3 interleaved app/reference runs of 60 s each (min–max in brackets).

| metric | app | reference mpv CLI | budget | result |
|---|---|---|---|---|
| CPU, % of all cores | 1.96 (1.93–2.00) | 0.35 (0.31–0.37) | < 3 | PASS |
| CPU, % of one core | 31.4 (30.9–31.9) | 5.6 (5.0–5.9) | small overhead over reference | **app ≈ 5.6× reference** |
| working set max, MB | 294.5 (294.2–294.9) | ≈ 193 | < 250 | **FAIL** |
| GPU local memory, MB | 307.2 | | | |
| seek-by-reload, median per run, ms | 96.5 (90.8–116.2) | | < 150 | PASS on median; 5 of 30 seeks > 150, worst 216 |
| open → first frame, ms | 260.6 cold, 186–194 warm | | < 250 | cold start just over |
| presents/s | 59.91 | | ≈ 60 | PASS, 0 black frames, 0 present errors |
| present interval P99 / max, ms | ≈ 32 / ≈ 45 | | | about 0.7 % of presents took 2 frames |

Soak (4.9 media-minutes, one pass of the 300 s EDL): the working set grows from ≈ 284 to ≈ 358 MB in 4 minutes (≈ 19 MB/min, peak 377 MB);
private bytes grow from ≈ 400 to ≈ 481 MB (peak 588 MB); GPU memory stays flat. It drops only at the loop reload (≈ 310 MB after). No
plateau within 5 minutes, so demuxer-cache fill and a leak can't be told apart yet.

**The "stall" at the loop end is a harness bug, not a playback problem.** With `keep-open=yes`, mpv pauses at the end of the file,
and `pause` is a player-wide property that survives `loadfile`. The loop reloaded the EDL, mpv showed the first frame in 48 ms,
and then sat paused (log: `playback restart complete … video=playing (paused)`) until the run ended 8 s later. The reload itself
was fast. Fix: set `pause=no` with the reload whenever playback should continue. The product should also react to the
`eof-reached` property change (observed, event-driven) instead of polling it every 100 ms as the spike does.
Seeks landed ≈ 1.2 s past the target in this run, compared with ≈ 0.42 s in S2.

Open for Phase 2 (each needs a look before the budgets can hold): the app's CPU overhead over plain mpv (managed render loop, probes and
stats sampling in the spike), RAM (libmpv demuxer/cache defaults, WinUI, D3D11 runtime), the seek tail, and a full 10-minute soak.
Maintainer 2026-11-07: RAM and CPU overhead are acceptable for now; only a real leak (usage that keeps climbing) must be fixed.

### How to run

Run **once, on an idle machine** (close games, browsers, Steam overlay; the script refuses to start above 10 % machine CPU unless `-Force`):

```powershell
spikes\001-winui-mpv-d3d11\perf\run-perf.ps1                 # ≈ 25 min: 3 interleaved (app, ref) repeats of 68 s + 10-min soak
spikes\001-winui-mpv-d3d11\perf\run-perf.ps1 -Smoke -Force   # ≈ 70 s wiring check, writes out/perf-smoke, labelled SMOKE
```
Outputs: `out/perf/` raw (`<tag>.csv` 2-s samples, `<tag>.json`, logs, EDLs) + `summary.md` / `summary.json` (median + min/max, hardware class only).
Measures: present stats/frame timing and presents/s at 1x 1440p60; process CPU time per media-second as % of one core and of all logical cores for
the **app** and the **reference** (mpv CLI, same `hwdec=d3d11va`, same EDL, same start); 10 seek-by-reload latencies per app run; working set, private
bytes and GPU local/non-local memory over a 10 media-minute soak. Budgets (plan S1): seek < 150 ms, open → first frame < 250 ms, CPU < 3 % of all cores at
1440p60 HEVC, RAM < 250 MB.

The per-run `.log` files name the GPU model: don't publish `out/`.

## Known limitations

- Overlay / black-frame checks depend on screen capture, so the window must be foreground and unobstructed; DWM composes XAML over the swap chain, so it can't be read back from the swap chain itself.
- `System.Drawing.Common` is a spike-only dependency; Phase 2 should use `Windows.Graphics.Capture` or drop the capture check.
- Seek-by-reload lands ≈ +0.42 s past the target (S2 finding); the player must subtract it or accept it.
- Only HEVC 1440p60 was exercised (S2's fixture); H.264 sessions, live (growing) sessions and UNC libraries are untested.
- Single GPU vendor; AMD/Intel behaviour of the D3D11 render API untested.

Verdict: PARTIAL. The D3D11 render API embedding works (part A 26/26). CPU, frame rate and median seek meet the budgets. RAM
fails (295 MB, still growing during the soak), and the app's CPU overhead over plain mpv is large.
