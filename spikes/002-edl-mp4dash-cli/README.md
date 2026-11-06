# Spike S2: `edl-mp4dash-cli`

Question: can **mpv ≥ 0.42**, fed an **EDL over the chunk files** (`!mp4_dash,init=` + `!new_stream`), play a 2 h Steam background
session whose `session.mpd` is stale, with instant start and seeks, A/V sync, live `audio-delay`, speed 0.25/2/4×, frame-step, gaps,
and a known `time-pos` origin? Plan row S2 (`.hermes/plans/2026-11-05_211500-steampoweredplayer.md`, §5 Phase 0).

**Verdict: PARTIAL** (details at the end). The EDL exactly as sketched in plan task 1.6 does **not** work (seeks land up to 3 s early,
audio is lost after every seek, gaps freeze for 30 s). A modified EDL ("lead EDL") plus a few rules works and meets the budgets with
hardware decode, except where noted.

## Setup (reproducible)

| Item | Value |
|---|---|
| mpv | shinchiro `mpv-winbuild-cmake` release `20261105`, asset `mpv-x86_64-20261105-git-3138fc8eea.7z` (34 199 875 bytes), mpv `v0.42.0-1-g3138fc8ee`, FFmpeg `N-128420-gbaa68e68b`. Extracted to `.tools/mpv/mpv-20261105/` (CLI `mpv.exe`; libmpv not needed here) |
| SHA-256 of the archive (computed locally; GitHub publishes no digest for it) | `89aa56f9769f81f783576183212e7b4802c91909ecf860b4468113cbfd106d93` |
| Machine class | desktop NVIDIA GPU (NVDEC/D3D11VA), 16 logical cores, NVMe, Windows 11. Library on a local SSD |
| mpv options | `--config=no --idle=yes --keep-open=yes --hr-seek=yes --osc=no --input-default-bindings=no --vo=gpu-next --gpu-api=d3d11 --ao=wasapi --volume=0 --geometry=640x360 --hwdec=d3d11va` (or `--hwdec=no` for CPU), JSON IPC over `--input-ipc-server`. A real (small) window and a real audio device (muted) are used, so the VO, hwdec and audio clock are exercised |
| Sessions (read-only, from the maintainer's real library) | **stale 2 h**: 2 400 chunks per track, first chunk number 1840, MPD says `startNumber=1`, duration `PT0.0S`, last two *video* chunks are zero-filled. **crashed**: zero-filled MPD, `.tmp` leftovers, its only video chunk is zero-filled. **gap**: none exists in the library, so a 30 s gap is built into the EDL (and a "listed but missing file" case) |
| Share-mode probe | runs on a 100-chunk **copy** under `.tools/fixtures-local/s2/` |

Tool: `S2Tool/` (C# 14 / .NET 10 console: scanner, EDL builder, mp4 box reader, mpv IPC driver, share probe, summariser).

```powershell
# build (from the repo root; tools stay inside .tools\)
. tools\env.ps1    # once the bootstrap phase exists; this spike used DOTNET_CLI_HOME / NUGET_PACKAGES under .tools\mpv\
dotnet build spikes\002-edl-mp4dash-cli\S2Tool -c Release -o spikes\002-edl-mp4dash-cli\out\bin

# EDL for one session folder (--validate skips zero-filled chunks, --lead = lead EDL)
dotnet spikes\002-edl-mp4dash-cli\out\bin\S2Tool.dll edl <RecordingRoot>\video\<session> out.edl --validate
dotnet spikes\002-edl-mp4dash-cli\out\bin\S2Tool.dll info <session>        # runs, tfdt, timescales, MPD state
dotnet spikes\002-edl-mp4dash-cli\out\bin\S2Tool.dll integrity <session>   # zero-filled / truncated chunks
dotnet spikes\002-edl-mp4dash-cli\out\bin\S2Tool.dll probe-selftest out\probe-control

# correctness runs (one pass, both EDL variants), then the timed runs: 3 repeats, variants interleaved, idle machine
spikes\002-edl-mp4dash-cli\run-all.ps1   -Mpv <mpv.exe> -Stale <stale-session> -Crashed <crashed-session> -Copy <fixtures-local>\s2\stale-100
spikes\002-edl-mp4dash-cli\run-timed.ps1 -Mpv <mpv.exe> -Stale <stale-session> -Crashed <crashed-session> -Reps 3
spikes\002-edl-mp4dash-cli\run-extra.ps1 -Mpv <mpv.exe> -Stale <stale-session>   # windowed-EDL reload seeks x3, memory growth x1
dotnet spikes\002-edl-mp4dash-cli\out\bin\S2Tool.dll summary spikes\002-edl-mp4dash-cli\out\final\lead spikes\002-edl-mp4dash-cli\out\final\classic
```

Single scenarios: `S2Tool run <explore|coldopen|prewarm|basic|seekmodes|seekwin|eof|speed|memgrow|gap|zero|crashed|hrprobe|hrfix|share> <session> --mpv <mpv.exe> --hwdec d3d11va|no --out <dir> --tag <name> [--lead]`.

## Which numbers are valid

* **Timing numbers from the first measurement pass are INVALID** and must not be cited: another worker ran NVENC encodes and a 16-thread
  VMAF in parallel and the maintainer used the GPU. Affected raw files (kept, not deleted, each folder has `INVALID-TIMINGS.txt`):
  `out/classic/`, `out/lead/`, `out/stale2h/`, `out/share/`, `out/crashed/`, `out/trial/` (all gitignored).
  Their *correctness* results stay valid and are used below (time-pos origin, A/V offsets, gap handling, share mode, decode-error analysis).
* **All timing numbers below come from `out/final/`**: recorded on the idle machine, **3 repeats per scenario, d3d11va and cpu interleaved
  inside each repeat**, pooled as median [min .. max]. The pooled data are committed in `results/summary-*.json` (per-repeat medians included).
  The classic-EDL baseline was run with hardware decode only.

## Findings

### 1. Time axes (answers "what does `time-pos` report at start?")
* `time-pos` at start = the first frame's pts = **first video chunk's `tfdt` − ~33 ms** (stale session: first chunk tfdt 5517.038 s, first
  frame `time-pos` 5517.004 s; audio chunk tfdt 5517.021 s). It is **not** 0. Chunk *n* starts at tfdt (n−1)·3 s + 0.037 s with no drift.
* mpv's `duration` property is unreliable with these EDLs (it first reports the EDL length from 0, later origin + length). Compute duration from the index (chunks × 3 s).
* `seek`, `start=` use the **EDL axis**; `time-pos` uses the **media (tfdt) axis**. In the plan's EDL (classic) these differ by the origin.

### 2. Why the plan's EDL fails (mpv source `demux_timeline.c`/`demux_edl.c`, v0.42.0)
For `mp4_dash`, mpv applies **no timestamp offset** to segments (`no_clip` true, `rebase` false). The EDL axis is only used to pick the segment;
the seek is then forwarded to the chunk with the EDL time while the chunk's own timestamps are tfdt-based. Measured consequences (classic EDL, 3 repeats):
* a seek lands on the **start of the 3 s chunk**, not on the target: paused exact seeks off by median −986 ms (min −2 770 ms), only 2 of 27 within 34 ms;
* the audio demuxer is then seeked with the video's raw pts (≈ 9117 s) on the 0–7194 s axis, i.e. to the **last segment**: audio never resumes (`audio-pts` unavailable after 40 of 40 seeks; mpv logs "delaying audio start 12708 vs 11155");
* a gap in the EDL freezes video for **29 s** until the timestamps catch up; a mid-play reload or EOF continuation replays 1.5–3 s (`mediaJump` −1 551 ms; EOF continuation lands at 5934.05 instead of 5936.99).

### 3. The fix: "lead EDL" (S2Tool `Edl.Build(lead: true)`)
Declare the **first part** as long as everything before it (`length = N0·3 s`) and **stretch the part before each gap** over the hole
(`length = 3·(next − n)`). Then the EDL axis equals the tfdt axis: `time-pos` == `seek` axis == session media time (UI time = `time-pos`,
or `time-pos − first tfdt` for a 0-based view), `duration` is exact from the start (e.g. 12 711 for a session whose first chunk is 1840 and last 4237),
trimming the head or growing the tail does not move the axis, and gaps are crossed instantly (`time-pos` 5576.05 → 5607.05 within 1 s, A/V in sync after).
Everything below is the lead EDL unless it says "classic".

### 4. Remaining mpv defect: revisiting an already-opened chunk (root cause found, workaround verified)
`switch_segment(.., init=true)` skips `demux_seek` for dash segments, so when playback advances into a chunk mpv **already opened earlier in the same
timeline** (a rewind, a repeated seek, a backward step) it continues from wherever that chunk's demuxer was left. Result: HEVC "Could not find ref with POC…",
audio PTS jumps of +1.6 s, time-pos jumps of +9…+15 s, audio 3–6 s ahead of video; it persists until the next load. Verified with 30 random seeks
(18 first visits, 12 revisits), 3 repeats × d3d11va/cpu, each followed by 3.5 s of playback:

| Positioning | first visits | revisits | failures | latency median [min..max] d3d11va | latency cpu |
|---|---|---|---|---|---|
| `seek … absolute+exact` | all good | **all 12 fail** | 12/30, every repeat | 59 ms [8 .. 171] | 226 ms [59 .. 456] |
| `loadfile <edl> replace -1 start=<t>` (whole-session EDL) | good | good | **0/30** | 133 ms [81 .. 272] | 291 ms [125 .. 511] |
| same with a 60-chunk window EDL around the target | good | good | **0/30** | **95 ms [43 .. 192]** | 247 ms [85 .. 463] |

Not caused by `--hr-seek-framedrop` (tested `=no`: identical), by the demuxer cache (`--demuxer-seekable-cache=no`: identical) or by hwdec (same errors with `--hwdec=no`).
Rule for the app: **every user seek = reload** (`loadfile` with `start=`, optionally a windowed EDL), or use `seek` only for chunks not opened since the last load.
Reloading also releases the accumulated file handles (see 7). `frame-back-step` is a backward exact seek, so stepping back **across a chunk boundary** hits the same defect
(deterministic: +12 s jumps every ~3 steps); inside a chunk it is fine. The app should implement back-step across a boundary as a reload at `time-pos − 1 frame`.

### 5. Picture correctness
Paused exact seeks (16 targets) compared with an independent ffmpeg decode of the same frame: **PSNR 40.8–45.1 dB** (screenshots of mpv's video output), frame landing
−8…+19 ms from the target (27 targets: all within 34 ms, one frame). No visible corruption in the passing cases.

### 6. Sessions that are not clean
* **Zero-filled chunks exist in the real library** (stale session: last two *video* chunks, full size, all zero bytes; crashed session: its only video chunk). A name-only scanner would include them.
  mpv tolerates them silently (stops at the end of the last good chunk, no error), but the scanner should check the first 8 bytes (S2Tool `validateHeads`: an 8-byte read per chunk). Crashed session with a name-only EDL: opens and plays audio only (video "Loading failed"), first restart after **264 ms (cpu) / 279 ms (d3d11va)**, no `end-file` error; with validation it has no playable segment.
* An EDL entry whose file is **missing** on disk (vanished mid-session): mpv skips it without error or a stall (≤ 1 s sampling resolution).
* `session.mpd` is not used at all.

### 7. Share mode, handles, memory (the Steam-cleanup question)
* **mpv opens chunk files with `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`.** Method: Restart Manager lists which files mpv holds; for each, a probe opens the file
  (OPEN_EXISTING, all share flags, never writes or deletes) asking for DELETE / write-data / read-data access: a sharing violation would mean the holder lacks that share bit.
  Result on 100 files over 4 phases: 0 denials for all three. The probe's sensitivity was proven with a control (`probe-selftest`: holders opened with Read, ReadWrite, Read|Delete, ReadWrite|Delete are classified correctly, 4/4).
* Whether a delete of such a file *succeeds* and what Steam sees is **not tested** (needs approval, see `needs_owner`).
* mpv **never closes played chunks** within one timeline: held chunk files grow with playback (3 after open → 200 after 40 s at 4×, i.e. both tracks of every chunk played; process handle count +1.7 per chunk).
  With share-delete this should not block a delete, but a deleted-while-open file stays "delete pending" (name and disk space) until mpv closes it. A reload closes them.
* Memory grows with played media: working set 206 MB at open → 236 / 279 / 321 / 356 / 397 / 431 MB after 160 / 320 / 480 / 640 / 800 / 960 s of media (d3d11va; cpu decode +160 MB higher). **≈ 0.23 MB per media-second**.
  A reload drops the handle count back (1 429 → 856) but only part of the memory (431 → 326 MB). `--demuxer-max-bytes=24MiB` did not change this. The 250 MB RAM budget holds only for the first ~3 minutes of sequential playback.

## Measured against the S2 criteria (idle machine, median [min .. max], n = 3 repeats unless noted)

| Criterion | Budget | Classic EDL (plan sketch), d3d11va | Lead EDL, d3d11va | Lead EDL, cpu (`hwdec=no`) |
|---|---|---|---|---|
| Open → first frame, **first open in a fresh process** | < 250 ms | 299 ms [283 .. 346] (n=15) | **302 ms [295 .. 333]** (n=15) ✗ | 320 ms [305 .. 354] ✗ |
| Open → first frame, **after a pre-warm** (tiny 2-chunk EDL opened at startup) | < 250 ms | n/a | **95 ms [89 .. 99]** (n=15) ✓ | 132 ms [129 .. 141] ✓ |
| Open → first frame, repeat open in the same process | < 250 ms | 81 ms [72 .. 88] | **80 ms [72 .. 91]** ✓ | 110 ms [106 .. 141] ✓ |
| Seek to 1:00:00 (first visit, fresh process) | < 150 ms | 3600 s is chunk-aligned | **7.6 ms [6.6 .. 16]** (n=15) ✓ | 51 ms [49 .. 71] ✓ |
| Seek anywhere, `seek` on first visits (40 seeks × 3) | < 150 ms | median 111 ms but wrong place | **66 ms [6 .. 142]** ✓ | 209 ms [48 .. 503] ✗ |
| Seek anywhere, reload with window EDL (any visit, 30 × 3) | < 150 ms | n/a | **95 ms [43 .. 192]**, 9 of 90 over 150 ms (whole-session EDL: 35 of 90) ~ | 247 ms [85 .. 463] ✗ |
| Frame-exact landing (paused, 27 targets × 3) | within 1 frame | median −986 ms ✗ | **−8 … +19 ms, 27/27 within 34 ms** ✓ | same ✓ |
| A/V after seeks (audio-pts − time-pos, 40 seeks × 3; constant offset −25 ms in every state incl. plain 1× playback, treated as a measurement bias) | in sync | audio missing after 40/40 ✗ | **max |offset| 32 ms, 0 over 80 ms** ✓ | 36 ms, 0 over 80 ms ✓ |
| Live `audio-delay` +0.3 / −0.2 / 0 s | live | – | **works**: shifts audio-pts − time-pos by about −300 / +200 ms, 0.1 ms command, 0 restarts ✓ | same ✓ |
| Speed 0.25 / 2 / 4× (3 start positions × 3, sampled every 250 ms) | works | – | median instantaneous rate **0.24 / 2.00 / 4.00** (overall 0.25 / 2.00 / 4.00), off-target samples 0, frames dropped 0 (4×: 0–1) ✓ | 0.24 / 2.00 / 4.00, 4× drops 1 [0..5]; CPU 25% / 184% / 364% of one core |
| Frame-step forward (10 × 3, and across a chunk boundary) | works | – | **16.7 ms/frame, latency 16 ms, no stall at the boundary** ✓ | 16 ms ✓ |
| Frame-step back | works | – | inside a chunk: −16.7 ms/frame, **129 ms** [98 .. 231]; across a boundary ✗ (see 4) | 221 ms |
| Gaps (30 s hole built into the EDL) | handled | **29 s frozen** ✗ | **crossed instantly**, A/V in sync ✓ (correctness run) | – |
| EOF → rebuilt EDL → `loadfile start=` | seamless | lands 2.94 s early (replay) ✗ | continues at **5937.02 right after 5936.99**; first frame **169 ms [158 .. 198]** after `loadfile` ✓ | 329 ms [304 .. 333] |
| Mid-play reload (CacheFollower rebuild) | seamless | blackout 162 ms, jump −1 551 ms ✗ | blackout **56 ms [49 .. 57]**, media jump **−28 ms** ✓ | 116 ms, −90 ms |
| `time-pos` origin | recorded | first frame pts ≈ first tfdt − 33 ms | same; **`time-pos` = session media time** | same |
| CPU at 1×, 10 s (1440p60 HEVC) | < 3 % | 7.8 % of one core | **5.2 % of one core [3.0 .. 5.3] = 0.32 % of all 16 logical cores** (mpv CLI with `gpu-next`; S1 measures the app's own render path) ~ | 77.7 % of one core (4.9 % of all) |
| RAM at start / after many seeks | < 250 MB | 207 MB | 205 MB WS (268 MB private) ✓ at start, **431 MB after 16 media-minutes** ✗ (7) | 391 MB at start ✗ |

`~` = depends on the reading of the budget (see open issues). Raw per-repeat medians: `results/summary-lead-edl.json`, `results/summary-classic-edl.json`.

## Consequences for the plan
1. **Task 1.6 (Edl builder) must change**: lead lengths (first part `N0·3`, stretch before gaps) instead of `length=3` everywhere; keep `!mp4_dash,init=` / `!new_stream` / `%len%` escaping as sketched (they work). The scanner must **validate chunk heads** (zero-filled chunks) as well as names/`.tmp`.
2. **Task 1.5 / Phase 2 playback**: UI time = `time-pos` (session time); duration from the index; **seek = reload** (`loadfile … start=`), preferably with a ~60-chunk window EDL rebuilt around the playhead; pre-warm mpv at app start (open a tiny EDL) so the first open is ~95 ms instead of ~300 ms; periodic reload (every few minutes) to bound handles and RAM.
3. Back-step across a chunk boundary and any "rewind" must use the reload path.
4. S1 (WinUI render API) should re-measure CPU and RAM with the app's own render path; this spike used mpv's own window.
5. Hardware decode is required for the seek budget (`hwdec=no` misses it by 1.5–3×: a seek decodes up to 3 s of 1440p60 HEVC). The plan's "CPU option everywhere" holds for playback (1×–4× keep up) but not for instant seeks.

## Experiments that need the maintainer's approval (not run: they delete files)
Both run **only on copies** under `.tools/fixtures-local/s2/` (never in `BackgroundRecordPath`), with mpv playing from the copy, using the lead EDL:
1. **Delete a chunk mpv has open.** Copy the 100-chunk fixture to `del-test`; play it (hardware decode, `--input-ipc-server`); after 20 s pick one chunk that Restart Manager lists as held by mpv
   (S2Tool `probe <copy> --pid <mpv pid>`); delete it with `File.Delete` (and once more with `Remove-Item`). Record: delete succeeds or sharing violation; whether the name still
   shows in a directory listing (delete-pending) and whether new opens fail; free-space change; whether playback continues across/into the deleted chunk; what changes after a reload (`loadfile start=`) or after mpv closes the file.
2. **Steam trims head chunks during playback.** Copy to `trim-test`; play at 1× from the first chunk; every 3 s delete the oldest chunk of the copy (simulating Steam's trim) for 60 s,
   including chunks mpv has open. Record: playback survives, errors in the mpv log, `time-pos` axis unchanged; then rebuild the EDL without the deleted chunks and `loadfile … start=<time-pos>`:
   continuity (gap in `time-pos`), blackout, and that no handle to a deleted chunk remains (Restart Manager).
Cleanup that only the maintainer may do (nothing was deleted by this spike): `spikes/002-edl-mp4dash-cli/out/` (2.8 GB: `out/bin*`, `out/trial`, the old invalid runs, `out/lead/shots` PNG/MP4 reference frames, mpv logs),
`.tools/fixtures-local/s2/stale-100` (810 MB copy), `.tools/mpv/mpv-20261105`'s `src-ref/` (mpv sources used for the root-cause analysis), `.tools/mpv/{dotnet-home,nuget}`.

## s2d follow-up: head trim on copies, gap handling, delete harness
Rule for this round: the worker never deletes anything, not even its own copies. Own copies live in `.tools/s2d/` and are listed for the maintainer to clean up.
New S2Tool commands (`Del.cs`, `Dx.cs`): `mkcopy` (copy only, refuses anything outside `.tools/s2d/`), `copycheck` (playback checks of a copy), `deltest` (experiments 1 and 2 above, driven by an
external `--delete-cmd` script; dry run without it). S2Tool has no file-delete or move API. How to run the live experiments: [DELETE-TESTS.md](DELETE-TESTS.md).

**(a) Head trim without deleting (done, d3d11va, lead EDL).**
| Check | Result |
|---|---|
| Copy of chunks 1925..1939 only (15 of the 100) plus init files | `tfdt` of every kept chunk identical to the untrimmed fixture (0 mismatches, 0 size mismatches) |
| `time-pos` axis | still equals `tfdt`: lead base moves to 3·(1925−1) = 5772 s, axis spread 20 ms (the 0.037 s chunk offset), so marker offsets stay valid |
| Open at first chunk, reload mid, revisit first chunk, reload at last chunk | all play; reload to `playback-restart` 46..351 ms; mpv log errors 0 |
| Copy with a hole (chunks 1915..1934, 1925 and 1926 missing) | `time-pos` jumps forward 6.149 s across the 6 s hole with 0 ms stall; reload after the hole works; errors 0 |

Consequence: head trimming needs no timeline bookkeeping. Rebuild the EDL from the chunks on disk, keep the axis, and a seek is the same `loadfile start=` as before.

**(b) Live delete experiments: pending maintainer run** (see DELETE-TESTS.md). Dry runs of the harness only (nothing deleted, so these say nothing about Windows delete behaviour):
* Experiment 1 on a 15-chunk copy: Restart Manager names mpv as the holder of the playhead chunk; playback continuous (max jump ±0.018 s); no handles after the reload.
* Experiment 2 on a 40-chunk copy: 20 ticks of 3 s, 40 "WOULD DELETE" lines; 223 samples over 58 s, max jump +0.029 s / −0.027 s, no stall; mpv log errors 0 at every tick; rebuilt EDL (20 chunks) reload in 87 ms, advanced 3.999 s in 4.012 s of wall time; no process holds any of the 40 files.
* Harness bug found and fixed on the way: the sampler task and the main loop wrote to the mpv IPC pipe concurrently (hang, then `TaskCanceledException`). `Mpv.CmdRaw` now serializes writes, has a 30 s reply timeout with a descriptive error, and records read-loop diagnostics.

## Open issues
* s2d: live delete behaviour (delete-pending, new-open errors, playback continuing across a deleted open chunk, handles after reload) is untested until the maintainer runs DELETE-TESTS.md.
* s2d: mpv logs a `[vo/gpu-next] Loading failed.` line at each reload; no error is counted and playback is fine, cause not investigated.
* Budget "CPU < 3 %": measured 5.2 % of one core, 0.32 % of all logical cores for mpv's own window; unclear which the plan means (S1).
* Seek budget with reload: 9 of 90 windowed reloads (35 of 90 whole-session reloads) exceeded 150 ms with hardware decode (max 192 ms windowed); CPU decode is out of budget (83 of 90).
* mpv documents `mp4_dash` as "may change at any time"; all of the above is pinned to 0.42.0 (the lead EDL relies on the unclipped-segment behaviour; re-test on every mpv bump). Fallback from the plan (libmpv `stream_cb` virtual fMP4) is untested.
* The −25 ms constant between `audio-pts` and `time-pos` could not be separated from a real offset; it does not change with seeks, speed or reloads. The `avsync` property reads 0 in this setup and is not usable.
* Windowed EDL: only the reload latency was measured; window size (60 chunks) was not tuned.
* Not tested: libmpv (only the CLI), network/UNC libraries, live (growing) sessions with Steam writing, H.264 sessions.

Verdict: PARTIAL
