<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# S2 live delete experiments (maintainer runs these)

**Status: RUN, results in README** (section "Live delete results (maintainer run)"). The maintainer ran both experiments live (tag `live2`; an earlier `live` attempt was refused by the path check and deleted nothing).
The harness was first verified in dry-run mode (it prints `WOULD DELETE <path>` and deletes nothing). The worker that wrote it is not allowed to delete files, and S2Tool itself contains no
delete or move API: the only thing that removes a file is the script **you** pass with `--delete-cmd`.

Questions these runs answer (see README, "Experiments that need the maintainer's approval"):
1. What happens when a chunk that mpv has open is deleted (sharing result, delete-pending, new-open failures, free space, playback, reload, mpv close).
2. What happens when Steam-style head trimming (oldest chunk every 3 s) runs during playback, and does a rebuilt EDL plus `loadfile start=` recover cleanly.

## Placeholders
| Placeholder | Meaning |
|---|---|
| `<S2Tool>` | `spikes/002-edl-mp4dash-cli/out/bin/S2Tool.exe` (build: `dotnet build spikes/002-edl-mp4dash-cli/S2Tool -c Release -o spikes/002-edl-mp4dash-cli/out/bin`) |
| `<FixtureDir>` | a session folder with `init-stream{0,1}.m4s` and `chunk-stream{0,1}-NNNNN.m4s` (the read-only S2 fixture, never a live Steam folder) |
| `<MpvExe>` | the pinned `mpv.exe` (0.42) |
| `<CopyDir>` | an empty destination whose path **contains `/.tools/s2d/`**, e.g. `<Repo>/.tools/s2d/del-exp1` |
| `<DeleteScript>` | your PowerShell script, called as `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <DeleteScript> <absolute chunk path>`. It receives the **absolute path of one chunk file** as `$args[0]`. It performs the removal you want to test (try both `[IO.File]::Delete` and the PowerShell cmdlet, in separate runs if you like), prints what happened to stdout, and exits 0 on success or non-zero on failure. S2Tool records stdout, stderr, exit code and duration (60 s limit; it never kills the script). |
| `<OutDir>` | where result files go, e.g. `<Repo>/spikes/002-edl-mp4dash-cli/out/s2d-live` |

Use forward slashes in every path. Each playback takes at most about 2 minutes. Run the experiments one at a time.

## Step 1. Make fresh copies (copy only)
Each experiment needs its own copy because it destroys chunks. `mkcopy` refuses a non-empty destination, a destination outside `/.tools/s2d/`
(unless `--anywhere`), and a destination equal to or inside the source.
```powershell
# Experiment 1: 15 chunks are enough (about 140 MB)
<S2Tool> mkcopy <FixtureDir> <CopyDir1> --from 1925 --last 1939
# Experiment 2: 40 chunks (about 340 MB): 20 get trimmed, 20 remain
<S2Tool> mkcopy <FixtureDir> <CopyDir2> --from 1900 --last 1939
```
Other `mkcopy` option: `--skip a-b,c` leaves out chunk numbers (gap tests). It writes `S2D-COPY.txt` into the copy; `deltest` refuses any folder without it.

## Step 2. Experiment 1: delete a chunk mpv holds open
```powershell
<S2Tool> deltest <CopyDir1> --exp 1 --delete-cmd <DeleteScript> --mpv <MpvExe> --out <OutDir>
```
Flow: build the lead EDL, play with hardware decode (`--hwdec d3d11va`, the default) for `--warm-sec` (default 20) seconds, ask Restart Manager which chunk mpv holds,
invoke `<DeleteScript>` on it, watch for `--after-sec` (default 10) seconds, do a `loadfile start=` reload, then quit mpv and check the file again.
Optional: `--stream 0|1` (which stream's chunk, default 0), `--tag <name>` (file name suffix, default `live`).

## Step 3. Experiment 2: head trim during playback
```powershell
<S2Tool> deltest <CopyDir2> --exp 2 --delete-cmd <DeleteScript> --mpv <MpvExe> --out <OutDir>
```
Flow: play at 1x from the first chunk; every `--tick-sec` (default 3) seconds for `--ticks` (default 20) ticks invoke `<DeleteScript>` on the oldest remaining chunk,
stream 0 first, then stream 1 (40 script calls in total). After the last tick the EDL is rebuilt without the deleted chunks and mpv does
`loadfile start=<time-pos>`; then handles, continuity and the final file states (after mpv quits) are checked.

## Dry run (optional sanity check, deletes nothing)
Omit `--delete-cmd`. The tag defaults to `dry`, so the files do not collide with live results.

## Output files (in `<OutDir>`; N = experiment, tag = `live` or `dry`)
| File | Meaning |
|---|---|
| `deltest-expN-<tag>.json` | The main result. Per delete: script stdout/stderr/exit code/ms, file state right after (`gone`, `listed-but-not-openable (delete-pending)`, `still-there`), the new-open result (`ok`, or exception and Win32 code: 2 not found, 5 access denied = delete-pending, 32 sharing violation), free-space delta, Restart Manager holders (PIDs), mpv log digest (errors, warnings, suspect lines). Plus continuity of `time-pos` (max forward/back jump, longest stall), the reload result (requested start, reload-to-restart ms, offset, advance vs wall time, hwdec, end-file events), handles still held after the reload, and file states after mpv exited. |
| `deltest-expN-<tag>-timepos.csv` | `time-pos` and `core-idle` sampled every 250 ms (`wall_ms,time_pos,core_idle`). Gaps or flat runs show stalls. |
| `deltest-expN-<tag>.mpv.log` | mpv verbose log of the whole run. |
| `deltest-expN-<tag>.edl` | The lead EDL that was played. |
| `deltest-expN-<tag>-rebuilt.edl` | The EDL rebuilt without the deleted chunks, used for the reload. |

Offsets of about 0.53 to 0.55 s between the requested start and the measured `time-pos` after a reload are the harness's 0.5 s read delay, not a seek error.
Free-space deltas only mean something on a live run, and only if nothing else on the drive changes at the same time.

## What to send back
1. `deltest-exp1-live.json` and `deltest-exp2-live.json`.
2. The two `-timepos.csv` files.
3. The two `.mpv.log` files (or only the lines around each delete if they are large).
4. The text of `<DeleteScript>` (so the delete API used is known) and any console output that looks like an error.
5. A one-line note about what you saw on screen (freeze, black frame, audio dropout), if anything.

The copies under `.tools/s2d/` and `<OutDir>` are yours to clean up. The worker never removes anything.
