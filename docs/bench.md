<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# `--selftest`, `--bench`, `--realcheck` and `--uicheck`

Four diagnostics commands of `SteamPoweredPlayer.exe`. `--selftest`, `--bench` and `--realcheck` run before any window exists;
`--uicheck` drives the real window. Each writes a text report (`--selftest`, `--bench` and `--realcheck` also a JSON report beside it:
`out.txt` and `out.json`) and exits with `0` only when nothing failed (`1`: a check failed or the run aborted, `2`: it crashed).
Reports hold **hardware class and numbers only**: no paths, game names, user ids, machine names, adapter names.
(`Redactor` scrubs every free text that came from an exception, ffmpeg or mpv.)

```powershell
SteamPoweredPlayer.exe --selftest out.txt                                        # synthetic, < 30 s
SteamPoweredPlayer.exe --realcheck out.txt [--library <BackgroundRecordPath>]    # real recordings, read-only, < 60 s
SteamPoweredPlayer.exe --uicheck out.txt [--captures <dir>]                      # real window on the real library, < 90 s
SteamPoweredPlayer.exe --bench out.txt [--library <BackgroundRecordPath>] [--quick]
```

Time targets (decision D-TEST, plan 2026-11-07): `dotnet test tests` < 2 min (opt-in real-library tests included), `--selftest` < 30 s,
`--realcheck` < 60 s, `--uicheck` < 90 s.

## `--selftest`: synthetic, no Steam needed (under 30 s)

ffmpeg's dash muxer generates one 58-chunk session (320x180, 30 fps H.264 + AAC, Steam's file names, 3 s segments, an IDR per
segment) in a temp directory; the recording shapes below are laid out from copies of it (`Core/Diagnostics/SyntheticSession`, also usable by tests).
The temp directory is removed at the end; nothing outside it is touched.

| Group | Checks (each prints PASS or FAIL with the measured value) |
|---|---|
| Scanner | normal, trimmed head with a stale manifest, stale manifest, zero-filled manifest, gap (two runs), live, crashed (zero manifest and stale `.tmp`), missing audio chunk, zero-filled chunk under a normal name |
| Playback (headless libmpv, `hwdec=no`, and the hardware decoder when present) | the lead EDL plays; seek-by-reload lands within one frame on about 35 targets including backward revisits and chunk boundaries; the same across the gap |
| Session behaviour | pause kept across reloads and frame-steps; speed 2x and 4x across chunk boundaries; Steam-style head trim (three head chunks deleted under playback, one every 0.6 s: no stall, no failed reload); trim past a paused playhead (jump to the new head, still paused); EOF on a live session that grows (a chunk every 1.2 s) continues playing |
| Export | `RangeSource` lossless 30 s (`ffmpeg -c copy`): video and audio duration within 0.1 s, A/V start offset within 0.1 s |
| Trickplay | one tile per chunk, one valid 1600x900 sheet, index loads |
| Later phases | listed as `SKIP (Phase 6)` so the gap stays visible: delete path guard |

Development aid: `SPP_SELFTEST_ONLY=play.live,scan` runs only the checks whose id starts with one of the prefixes (the report then lacks the rest).
The last INFO line lists every step's time, slowest first. To stay under 30 s (D-TEST) the scanner cases are built and scanned in
parallel, the read-only playback checks share one synthetic session, playback windows are 1 s, and the hardware-decode seek pass takes
every 4th target and stops after 2.5 s once 4 seeks are done (a game using the GPU makes each copy-back reload take up to a second).

Headless means `vo=null`; plain `hwdec=d3d11va` silently decodes in software without a video output, so the hardware row uses `d3d11va-copy`
and reports mpv's `hwdec-current`.

## `--bench`: the plan §5 budgets on a real library

The library is **read-only**: it is taken from `--library`, else `SPP_REAL_LIBRARY`, else Steam's configured `BackgroundRecordPath`.
The data folder (index, scan cache, art, trickplay sheets, export scratch) is a temp directory that is deleted at the end. Sessions are
only read (mpv, the scanner, `RangeSource`). The session measured is the longest finished one (never printed by name).
`--quick` shortens the soak to one minute, the CPU windows to 10 s and trickplay to 300 chunks.

Video output: when hardware decode works, mpv renders with `vo=gpu` (D3D11) into a hidden 1280x720 window and decodes with
`hwdec=d3d11va`, zero-copy into mpv's renderer as the app does; otherwise `d3d11va-copy` with `vo=null`; otherwise software. The XAML swap
chain of the Player page is **not** included: the Player page measures its own open and seek.

| Metric | Method | Gate |
|---|---|---|
| Library visible from index | cold scan with an empty temp data folder (reported); then a new `LibraryService` on the same folder: time until the saved index is raised (warm) | warm < 400 ms |
| Open to first frame | new libmpv, pre-warmed (`PreWarmAsync`) then `OpenAsync`; also fresh, without the pre-warm (reported); 10 runs (5 quick), median | pre-warmed median < 250 ms |
| Seek | 60 seeks (30 quick) through `PlaybackSession.SeekAsync`, half anywhere in the session, half scrubbing near the last target; median, p90, max, count over budget; also with CPU decode (reported) | p90 < 150 ms (plan: "anywhere"; S2 measured 9 of 90 over with hardware decode, so the gate is the 90th percentile) |
| RAM (D-RAM) | **base**: the process playing the session with a 1 MiB demuxer cache (mpv, decoder and renderer included), max of 2.5 s of samples; then the working set every 5 s (2 s quick) during 5 min (1 min quick) of playback with the session's own cache plan and a forced reload every minute (20 s quick); peak, end | peak <= (base + `DemuxerCache.ExpectedCacheBytes(bitrate)`) x 1.15 |
| Leak signal | least-squares slope of the samples after the warm-up (90 s, 20 s quick); a falling slope (a reload emptied the cache) is not a leak | < 2 MB/min |
| CPU | process CPU time per media second over a 30 s (10 s) window after 3 s of warm-up, as % of one core and of all logical cores, with hardware and with CPU decode; and the mpv CLI (`.tools/mpv/<version>/mpv.exe`) with the same EDL, hwdec and video output on the same window | reported (relative to the reference), not gated |
| Lossless 30 s export | `RangeSource` of both streams to temp files, then `ffmpeg -c copy`; 3 runs, median; the output must be 30 s +-0.5 | < 1 s |
| Trickplay | `Trickplay.GenerateAsync` over the longest session with the app's default decode mode, extrapolated to 2 400 chunks (2 h) | < 40 s per 2 h |

D-RAM (maintainer, 2026-11-07): RAM over 250 MB is accepted. RAM is a fixed base plus the demuxer cache, whose size follows the
bitrate (`Core/Playback/DemuxerCache`: 20 s forward + 10 s back x 1.25 headroom, clamped; about 100 MB at Steam's ~22 Mbit/s). The
gate is therefore peak <= (base + expected cache) x 1.15, and the leak slope; the fixed 250 MB is only mentioned in the note. The
`before playback` figure (no mpv yet) is reported too; the soak runs first, before the other sections, so it is not inflated by their
residue.

Machine load is not controlled; run on an idle machine for numbers worth recording.

## `--realcheck`: the real recordings, read-only (under 60 s)

The library comes from `--library`, else `SPP_REAL_LIBRARY`, else Steam's `BackgroundRecordPath`, and is only read (scanned into a
temp data folder that is deleted at the end). It uses the **shortest saved clip** of at least 6 s and the **last 10 s of the active**
(else the newest) **cache session**, with a real libmpv rendering into a hidden window (`hwdec=d3d11va`, software when that fails).

| Check | Pass when |
|---|---|
| open -> first frame | a frame arrives (time reported) |
| audio | mpv has an audio pts within 0.1 s of the video no later than 0.2 s of media after the first frame |
| UI position | mpv `time-pos` minus the media start is 0 at the clip's head (the tail: duration - 10 s), within 1 frame |
| UI duration | `UiDuration` equals the index duration (+-1 frame; a live session may only grow) |
| +5 s / -5 s | `SeekRelativeAsync(+-5)` lands on the snapped target within 1 frame (mpv `time-pos`) |
| frame-step | three steps, each 1 frame +-half a frame (fps from `container-fps`, else `estimated-vf-fps`) |
| A-B loop | A = 1 s, B = 3 s (after the opened point): wraps from B to A, never more than 2 frames past B or before A |
| markers | every marker placed on the timeline lies inside [media start, media end]; the index's count agrees; whole library: parsed vs placed |
| timecode | `HH:MM:SS.FF`, fixed width, FF = 1-based frame within the second, at 30 and 60 fps (`TimecodeRules`); **SKIP** until the Player's formatter is registered in `RealCheck.Formatter` |
| RAM | D-RAM on the cache tail: base with a 1 MiB cache, then two passes of the tail looped by mpv's ab-loop; peak <= (base + cache) x 1.15. The slope of the second pass is reported only (10 s is too short to gate a leak; `--bench` gates it) |
| gap | a seek into a gap lands on the next run's start; **SKIP** "no gap session found" when the library has none |

## `--uicheck`: the real window (under 90 s)

Starts the app with its normal settings (the real data folder and library) and drives it through UI Automation peers, found by
`AutomationProperties.AutomationId`, in three child processes so a crash in one segment does not hide the others:

1. `clip`: Library -> the shortest clip's tile (`LibraryClipTile`) -> Player (axis, timecode text F2, `PlayerForward5`, A-B with
   `PlayerAbLoop` at 1 s and 3 s) -> `PlayerBack`.
2. `settings`: `NavSettings` -> `NavLibrary`, three times.
3. `cache`: the active cache session's block (`LibraryCacheBlock`) -> Player -> `PlayerBack`.

A segment fails on a crash or hang of its process (exit code and step are reported; a WinUI fail-fast `0xC000027B` has no managed
exception, the last first-chance exceptions are printed when there were any), an unhandled or unobserved exception, a wrong page, or
a title-bar chip (`TitleChips`) whose width changes while the set of chips stays the same (F10). A missing automation element is a FAIL;
the segment then continues through `MainWindow.NavigateToPlayer` so the rest still runs. Window captures (`PrintWindow`, BMP) go to
`--captures` (default `%TEMP%\SteamPoweredPlayer-uicheck`), never into the repository: they show real games.

Automation ids: `NavLibrary`, `NavSettings`, `TitleChips` (MainWindow); `LibraryGameList`, `LibraryClipTile`, `LibraryCacheBlock`
(not there yet: the cache strip's blocks, agent C) (LibraryPage); `PlayerBack`, `PlayerPlayPause`, `PlayerBack5`, `PlayerFrameBack`,
`PlayerFrameForward`, `PlayerForward5`, `PlayerTimecode`, `PlayerAbLoop` (PlayerPage).

## Results

Fill in one block per run (hardware class only, as printed in the report header; no machine names).

| | |
|---|---|
| Date, build | |
| Hardware class | CPU class, logical cores, ISA; GPU vendor (discrete/integrated); Windows version |
| Decoder, video output | |
| Session | chunks, minutes |

| Metric | Budget | Result | PASS / FAIL |
|---|---|---|---|
| Library visible from index (warm) | < 400 ms | | |
| Library cold scan (reported) | n/a | | |
| Open to first frame, pre-warmed (median) | < 250 ms | | |
| Open to first frame, fresh (reported) | n/a | | |
| Seek p90 (median, max, over budget) | < 150 ms | | |
| RAM peak after 5 min with periodic reload (base, expected cache) | <= (base + cache) x 1.15 | | |
| RAM slope after warm-up | < 2 MB/min | | |
| CPU, app, hardware decode (% of one core / % of all) | relative | | |
| CPU, mpv CLI reference, hardware decode | relative | | |
| CPU, app, CPU decode | relative | | |
| CPU, mpv CLI reference, CPU decode | relative | | |
| Lossless 30 s export | < 1 s | | |
| Trickplay, extrapolated to 2 h | < 40 s | | |
