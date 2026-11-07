<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# `--selftest` and `--bench`

Two headless commands of `SteamPoweredPlayer.exe`. Both run before any window exists, write a text report and a JSON report beside it
(`out.txt` and `out.json`), and exit with `0` only when nothing failed (`1`: a check failed or the run aborted, `2`: it crashed).
Reports hold **hardware class and numbers only**: no paths, game names, user ids, machine names, adapter names.
(`Redactor` scrubs every free text that came from an exception, ffmpeg or mpv.)

```powershell
SteamPoweredPlayer.exe --selftest out.txt
SteamPoweredPlayer.exe --bench out.txt [--library <BackgroundRecordPath>] [--quick]
```

## `--selftest`: synthetic, no Steam needed (about 1 minute)

ffmpeg's dash muxer generates one 58-chunk session (320x180, 30 fps H.264 + AAC, Steam's file names, 3 s segments, an IDR per
segment) in a temp directory; the recording shapes below are laid out from copies of it (`Core/Diagnostics/SyntheticSession`, also usable by tests).
The temp directory is removed at the end; nothing outside it is touched.

| Group | Checks (each prints PASS or FAIL with the measured value) |
|---|---|
| Scanner | normal, trimmed head with a stale manifest, stale manifest, zero-filled manifest, gap (two runs), live, crashed (zero manifest and stale `.tmp`), missing audio chunk, zero-filled chunk under a normal name |
| Playback (headless libmpv, `hwdec=no`, and the hardware decoder when present) | the lead EDL plays; seek-by-reload lands within one frame on about 35 targets including backward revisits and chunk boundaries; the same across the gap |
| Session behaviour | pause kept across reloads and frame-steps; speed 2x and 4x across chunk boundaries; Steam-style head trim (a head chunk deleted every 3 s under playback: no stall, no failed reload); trim past a paused playhead (jump to the new head, still paused); EOF on a live session that grows continues playing |
| Export | `RangeSource` lossless 30 s (`ffmpeg -c copy`): video and audio duration within 0.1 s, A/V start offset within 0.1 s |
| Trickplay | one tile per chunk, one valid 1600x900 sheet, index loads |
| Later phases | listed as `SKIP (Phase 3/6)` so the gap stays visible: presets, audio-sync detect and fix, interrupted download, delete path guard, colour range per encoder |

Development aid: `SPP_SELFTEST_ONLY=play.live,scan` runs only the checks whose id starts with one of the prefixes (the report then lacks the rest).

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
| RAM | working set of the process sampled every 5 s during 5 min (1 min quick) of playback with a forced reload every minute (20 s quick); baseline, peak, end | peak < 250 MB |
| Leak signal | least-squares slope of the samples after the warm-up (90 s, 20 s quick) | < 4 MB/min |
| CPU | process CPU time per media second over a 30 s (10 s) window after 3 s of warm-up, as % of one core and of all logical cores, with hardware and with CPU decode; and the mpv CLI (`.tools/mpv/<version>/mpv.exe`) with the same EDL, hwdec and video output on the same window | reported (relative to the reference), not gated |
| Lossless 30 s export | `RangeSource` of both streams to temp files, then `ffmpeg -c copy`; 3 runs, median; the output must be 30 s +-0.5 | < 1 s |
| Trickplay | `Trickplay.GenerateAsync` over the longest session with the app's default decode mode, extrapolated to 2 400 chunks (2 h) | < 40 s per 2 h |

S1 note (RAM and CPU): the maintainer accepted the RAM and CPU overhead of mpv's demuxer cache unless it is a leak. The RAM budget still
prints PASS or FAIL against 250 MB; the slope line is what says "leak". The process working set includes the .NET runtime and the
Windows App SDK, so the `before playback` figure is the floor; the soak runs first, before the other sections, so that figure is not
inflated by their residue.

Machine load is not controlled; run on an idle machine for numbers worth recording.

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
| RAM peak after 5 min with periodic reload | < 250 MB | | |
| RAM slope after warm-up | < 4 MB/min | | |
| CPU, app, hardware decode (% of one core / % of all) | relative | | |
| CPU, mpv CLI reference, hardware decode | relative | | |
| CPU, app, CPU decode | relative | | |
| CPU, mpv CLI reference, CPU decode | relative | | |
| Lossless 30 s export | < 1 s | | |
| Trickplay, extrapolated to 2 h | < 40 s | | |
