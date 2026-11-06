<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Spike S5: timeline-alignment

**Question (plan Phase 0, row S5):** given sessions with deaths/kills, when timeline markers are mapped with the UTC rule
`entry.time − (sessionStartUtc − timelineStartUtc)`, do they land within ±1 s of the event you can see in the video?

Throwaway spike. Reads `<RecordingRoot>` strictly read-only (files opened with read + write + delete sharing, short reads, copied
into `out/cache2/` first). No downloads: only the .NET SDK and ffmpeg that were already installed.

| Tool | Version |
|---|---|
| .NET SDK | 10.0.401 |
| ffmpeg / ffprobe | 9.0 (gyan.dev full build) |
| Machine class | desktop, NVIDIA GPU, NVMe (frame extraction is CPU decode, timing is not measured here) |

## TL;DR (what a Phase 2 implementer needs)
1. **The simple rule is right in shape and holds within ±1 s on every marker I looked at** (32 point markers, worst measured error
   0.89 s; the two composite markers are ranges, see 4).
   No systematic offset exists beyond sub-second rounding.
2. **But the folder-name rule is quantised to whole seconds** (both the bg folder and the timeline name are truncated to a second),
   so its error is anywhere in (−1 s, +1 s). Measured over 18 sessions: −0.61 … +0.59 s, 4 of 18 beyond ±0.5 s.
   Steam stores the exact value. **Prefer it**; fall back to names only when it is unavailable:
   * live cache session: `gamerecording.pb` → field 1 (the entry whose field 1.1 is the `timeline_…` id) → field 5 → **field 10** = exact
     bg-start offset in the timeline, in ms (e.g. `57332` where the names say 57 s);
   * clip: `clip.pb` → field 1 → field 5 → **field 10** (same meaning, same unit).
   With the exact value the visible event starts **+0.09 s** after the mapped time (range −0.15 … +0.30 s, n = 11, ±0.1 s reading error).
3. `position_in_session = entry.time/1000 − offset` is **absolute session time = the media's `tfdt`**, not an index into the chunks that
   happen to be on disk. Chunk N starts at (N−1)·3 s (+≤ 8 ms) in live sessions, in **trimmed** sessions (chunk 1881 starts at 5640.006 s),
   and in clips (chunk numbering restarts at 1, `tfdt` does not: chunk 1 of one clip starts at 585.0 s). UI time = position − first
   segment's `tfdt`, as in the plan (§3 step 4).
4. **Composite events are ranges, not points.** `cs2_double_kill` / `cs2_multi_kill` carry `duration` (≈ 8 s). `time` is the start of
   the span: the first kill is ≈ **2 s after** `time` and the second ≈ 6 s after, the span ends after the last kill. Single
   `cs2_death`, `cs2_gun_kill`, `cs2_knife_kill`, `cs2_taser_kill` have `duration 0` and are points.
5. Seek lead-in: since the event is visible about 0.1 s after the mapped time, "jump to marker" should land 2–3 s earlier anyway.

## Corrected rule
```
offsetMs   = gamerecording.pb[timeline].bg.field10   // live session
           | clip.pb.field1.5.field10                // clip
           | (bgStartUtc − timelineStartUtc) × 1000  // fallback from folder names, ±1 s
positionS  = entry.time / 1000 − offsetMs / 1000     // = media tfdt, absolute
span       = entry.duration > 0 ? [positionS, positionS + duration/1000] : point
```
Also confirmed on all 193 session/timeline pairs in the library: `daterecorded` (unix s) always equals the UTC time in the timeline
file name, so the name parse is a safe fallback and the timeline start has no hidden offset. A timeline can start up to ~113 s
before its bg folder.

`gamerecording.pb` field 1.5 exists only for sessions whose cache folder is still present (3 of 168 index entries here). Its field 2 is
the timeline offset of the first *retained* media (trimmed session: field 2 = field 10 + 5640 s), field 3 the retained duration (ms), so
use **field 10**, not field 2.

## Method
1. `Align survey` lists every session folder (library + clips) with its matching timeline (same game id, latest start ≤ session start,
   < 30 min earlier), the name-derived offset and the number of death/kill-like events.
2. Only **two** full cache sessions with kill/death events exist in this library (A: 49 min, B: 67 min). The third set is a **pool of
   7 clips** (11 markers) because every clip carries its own chunks, its own copy of the timeline and `clip.pb`. This is a deviation
   from "3 sessions" and should be repeated when more sessions exist.
3. For each marker `Align sheet` byte-concatenates `init-stream0.m4s` + the chunks around it, seeks accurately (real first pts from
   ffprobe) and renders a contact sheet of frames at −2 s … +2 s (step 0.5 s, 9 frames, labelled with the offset from the marker).
   "Fine" sheets use 0.1 s steps over −0.6 … +0.6 s.
4. I looked at each sheet and noted when the event first appears. Cues in the footage (not committed, see below): the new row in the
   kill feed (top right), the money counter changing, the enemy collapsing; for deaths, the screen turning red then black with the
   killer card, and the kill-feed row with a red frame. Resolution: ±0.25 s coarse, ±0.1 s fine.
5. Rule A = name-derived offset (the plan's rule); rule E = exact offset. A uses rule A as generated, B and C use rule E; results are
   converted to the other rule with the known difference (`nameOffset − exactOffset`) and cross-checked: B's fine sheets were taken
   with rule A and gave +0.2/+0.35, the conversion from the E-sheets predicts the same.

## Results (events onset relative to the mapped time; negative = event is *before* the mapped time)
Offsets: A name 57 s / exact 57.332 s (error of names −0.332 s); B name 42 s / exact 41.794 s (+0.206 s).
Clips: the exact offset is in `clip.pb` (name errors per clip: −0.513, +0.330, +0.123, +0.145, +0.590, +0.086, +0.462, −0.259, −0.435,
+0.226, −0.610, −0.413, +0.212, −0.526, −0.398).

### Session A (live cache, name rule used for the sheets), 10 point markers
| Marker | Kind | Coarse bin vs rule A | Fine vs rule A | vs rule E |
|---|---|---|---|---|
| 542 | death | (−0.5, 0] | | (−0.17, +0.33] |
| 550 | kill | (−0.5, 0] | | (−0.17, +0.33] |
| 555 | death | (−0.5, 0] | −0.2 | +0.13 |
| 562 | kill | (−0.5, 0] | −0.2 | +0.13 |
| 561 | death | (−0.5, 0] | | (−0.17, +0.33] |
| 570 | kill | (−0.5, 0] | | (−0.17, +0.33] |
| 574 | death | (−0.5, 0] | | (−0.17, +0.33] |
| 580 | kill | (−0.5, 0] | | (−0.17, +0.33] |
| 592 | death | (−0.5, 0] | −0.3 | +0.03 |
| 604 | kill | (−0.5, 0] | −0.3 | +0.03 |

Two composite markers (585, 616, `duration` 8.3 s / 8.0 s): first kill at +2.0 s, second at +6.0 s after `time` (0.5–1 s steps).

### Session B (live cache, exact rule used for the sheets), 11 point markers
| Marker | Kind | Coarse bin vs rule E | Fine vs rule E |
|---|---|---|---|
| 633 | death | | 0.0 (dark +0.2, feed +0.3 vs rule A) |
| 647 | kill | | +0.15 |
| 654, 672, 682, 706, 719, 743 | death | (−0.5, 0] | |
| 666 | knife kill | (−0.5, 0] | |
| 697, 737 | kill | (−0.5, 0] | |

### Clip pool C (exact rule from `clip.pb`), 11 point markers from 7 clips
| Marker (clip) | Kind | Coarse bin vs rule E | Fine vs rule E | Under rule A |
|---|---|---|---|---|
| 87 | death | (−0.5, 0] | −0.15 | −0.66 |
| 38 | taser kill | (0, +0.5] | | |
| 40 | kill | (0, +0.5] | +0.30 | +0.63 |
| 379 | death | (0, +0.5] | +0.30 | **+0.89** |
| 15 | kill | (−0.5, 0] | | |
| 353 | taser kill | (−0.5, 0] | | |
| 893 | death | (−0.5, 0] | +0.15 | −0.26 |
| 1167, 1170 | kill | (−0.5, 0] | | |
| 1169 | taser kill | (−0.5, 0] | | |
| 1171 | kill | (−0.5, 0] | −0.05 | +0.16 |

### Summary
| Quantity | Value |
|---|---|
| Point markers examined | 32 (A 10, B 11, C 11), plus 2 composite |
| Max error, rule E | ≤ 0.5 s coarse, ≤ 0.30 s where fine |
| Max error, rule A (plan's rule) | 0.89 s measured (marker 379 in the clip pool); the other fine readings are ≤ 0.66 s, and the coarse bins alone bound every remaining marker to ≤ 0.94 s |
| Mean residual with exact offset | +0.09 s (n = 11 fine readings: 0.13, 0.13, 0.03, 0.03, 0.0, 0.15, 0.30, 0.30, −0.05, −0.15, +0.15) |
| Name-offset error, 18 sessions | −0.61 … +0.59 s, mean −0.05 s, 4 of 18 beyond ±0.5 s |
| Theoretical worst case of rule A | (−1, +1) s from names plus ≈ 0.35 s event lag, so it can exceed ±1 s in rare cases |

Also observed (2 samples, not validated): `mtime(init-stream0.m4s) − daterecorded` is 0.57 s and 0.75 s above the exact offset, so file
times could give a ±0.1 s fallback without Steam's index. Network copies may not keep mtimes; treat as an idea only.

## What is not committed
Frames and contact sheets live in `out/` (gitignored). They are game footage with other players' names, so no screenshots are in the
repository. To inspect: `out/sheets2/<set>/m<id>_<icon>_p<shift>.jpg`.

## Reproduce
Run from this folder in PowerShell. `<RecordingRoot>` is the `BackgroundRecordPath` of the Steam client.
```powershell
dotnet build Align/Align.csproj -c Release -o out/bin
dotnet out/bin/Align.dll survey  <RecordingRoot>                       # sessions + timelines + offsets
dotnet out/bin/Align.dll offsets <RecordingRoot>/gamerecording.pb       # name-vs-exact offset error (3 live sessions)
dotnet out/bin/Align.dll clips   <RecordingRoot>                        # clip windows, exact offsets, name errors
dotnet out/bin/Align.dll markers <SessionDir> <TimelineJson> death,kill # markers with name-rule positions
# frames -2..+2 s, 0.5 s step, marker ids or auto:N or all (clips); optional span step shift icons
dotnet out/bin/Align.dll sheet <SessionDir> <TimelineJson> <SetName> 542,550 2 0.5 0
# exact offset for a live session (seconds) instead of the name rule:
$env:SPIKE_OFF = '41.794'; dotnet out/bin/Align.dll sheet <SessionDir> <TimelineJson> B auto:12 2 0.5 0
dotnet out/bin/Align.dll pb <file.pb>                                   # generic protobuf dump (gamerecording.pb, clip.pb)
```
For clips (`<RecordingRoot>/clips/clip_*/video/bg_*`) `sheet` reads `clip.pb` itself (field 1.5.10 as the offset, `tfdt` of chunk 1).
`SPIKE_OFF` overrides the offset (seconds). Fine passes: `... <SetName> <ids> 0.6 0.1 0`.
Contact-sheet file names show the shift with one decimal (`m0.2` is −0.25 s).

## Notes for the maintainer
* An earlier attempt left `align.py` (Python, not allowed here) in this folder. It is untouched and not committed; the C# tool replaces it.
* Phase 2 should read `gamerecording.pb` field 1.5.10 (it is already planned to parse that file); keep the name rule as the fallback.
* The plan's Given/When/Then for S5 says "3 sessions". Only two cache sessions with events exist here; repeat with more as the library grows.

Verdict: VALIDATED
