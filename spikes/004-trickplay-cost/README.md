<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Spike S4: `trickplay-cost`

**Question (plan, Phase 0, S4):** given the init segment plus the first 512 KB of each chunk, decoded keyframe-only on the GPU in
one long-lived ffmpeg, is the cost ≤ 15 ms and ≤ 0.6 MB per chunk? Is the first 512 KB always enough to hold the whole IDR?

Short answer: **time budget passes with a 7x margin; the "first 512 KB" premise is false**. 14.9 % of chunks have an IDR that does
not fit in 512 KiB, so a fixed prefix needs 1 MiB (1.0 MB/chunk, over the byte budget). An **adaptive read** (read the moof, then
exactly the first fragment) is correct for 100 % of chunks and costs 0.27–0.35 MB/chunk on average.

## Hardware and software (class only)
Desktop PC, 16 logical CPU threads, desktop NVIDIA GPU (NVENC-capable, NVDEC HEVC), local SSD, Windows 11. ffmpeg/ffprobe 9.0 from
PATH, .NET SDK 10.0.401. Nothing was downloaded for this spike, so there are no pinned artefacts or hashes to record.

## Test data (read-only; never copied, never committed)
Real Steam background-cache sessions, all HEVC Main 2560x1440 59.94 fps `yuvj420p` (full range), ~22 Mbps. Chunks were opened with
read + write + delete sharing and only their first bytes were read.

| Session | Chunks | Size | Notes |
|---|---|---|---|
| A | 2400 (2398 usable) | 19.3 GB | a full 2 h cache session; the two last chunks are zero-filled (crash leftover) |
| B | 984 | 7.8 GB | |
| C | 1352 | 10.8 GB | still being recorded while scanned (read in place, no problem) |
| D | 64 | 0.6 GB | |

Total 4798 usable chunks. The measurement matrix below was run on A; B, C, D were scanned for the IDR size distribution only.

## Findings about the format (new, not in `docs/steam-recording-format.md` yet)
- A chunk is **one `moof`+`mdat` per frame** (one sample per fragment), not one big fragment. The first fragment is the IDR:
  `moof` is 108 bytes, `mdat` payload is NALs `VPS SPS PPS SEI IDR_W_RADL` (types 32 33 34 39 19) in all 4798 chunks.
- The `tfhd` carries `default_sample_size` / `default_sample_duration` / `default_sample_flags`; `trun` has one entry and no
  per-sample size. The IDR size is therefore in `tfhd` (and in the `mdat` box header).
- **The first fragment can be fed to ffmpeg byte for byte**: `init-stream0.m4s` + `[moof mdat]` of each chunk is a valid fMP4
  stream for the mov demuxer. No rebuilding is needed. ffmpeg decodes it keyframe-only because nothing else is sent.
- IDR (first fragment) size varies a lot: 1 KB (static scene) … 906 KB.

## How to reproduce
```bash
# from the repo root, git-bash. SessionDir = a <BackgroundRecordPath>/video/bg_* folder (Windows path, forward slashes)
export NUGET_PACKAGES=<ToolsDir>/s4/nuget DOTNET_CLI_HOME=<ToolsDir>/s4/dotnet-home
cd spikes/004-trickplay-cost
dotnet build Spike.csproj -c Release
./run.sh       <SessionDir> h2     # scan + decode matrix, writes out/h2_*.txt (about 4 min, mostly the 1-thread CPU run)
./run_sizes.sh <SessionDir> h2sz   # sprite sheet size variants
dotnet bin/Release/net10.0/Trickplay.Spike.dll scan <SessionDir> out/x.csv 2097152   # only the prefix statistics
```
Files: `Fmp4.cs` (first-fragment parser), `Program.cs` (`scan` and `decode` modes), `run.sh`, `run_sizes.sh`. `out/` is gitignored.

The decode mode starts **one** ffmpeg and keeps it for the whole session:
`ffmpeg -hwaccel d3d11va -i pipe:0 -an -fps_mode passthrough -vf scale=320:180:flags=fast_bilinear,format=yuvj420p,tile=10x10 -c:v mjpeg -q:v 5 sheet_%04d.jpg`
(raw mode replaces the tail with `-f rawvideo pipe:1`, so frame arrival times can be recorded). A producer task reads the
prefixes (bounded channel of 64) while the writer pipes them to ffmpeg's stdin.
Per-chunk time = steady-state inter-arrival time of 320x180 frames on stdout (first 10 frames skipped), and feed-to-exit wall time ÷
chunks. Hardware decode was confirmed with `-v verbose` (decoder `hevc` selected "because of requested hwaccel method", hardware
frames `nv12`, 0 decode errors) and by forcing `-hwaccel_output_format d3d11`, which also works.

## Results

### 1. Does the first 512 KB contain the whole IDR? No.
Bytes needed from the start of the file = `moof` + `mdat` of the first fragment (always `moof` at offset 0).

| Session | p50 | p90 | p99 | max | fail @ 512 KiB | fail @ 640 KiB | fail @ 768 KiB | fail @ 1 MiB |
|---|---|---|---|---|---|---|---|---|
| A (2398) | 247 KB | 444 KB | 658 KB | 824 KB | **132 (5.5 %)** | 28 | 4 | 0 |
| B (984) | 301 KB | 551 KB | 689 KB | 865 KB | **261 (26.5 %)** | 33 | 4 | 0 |
| C (1352) | 323 KB | 551 KB | 688 KB | 906 KB | **310 (22.9 %)** | 40 | 1 | 0 |
| D (64) | 359 KB | 532 KB | 570 KB | 584 KB | **11 (17.2 %)** | 0 | 0 | 0 |
| **All (4798)** | | | | **906 KB** | **714 (14.9 %)** | 101 (2.1 %) | 9 (0.2 %) | **0** |

(KB = 1024 bytes.) The cost of a failure: the IDR is cut, so the chunk gives no thumbnail (the spike skips it and counts it).
**Minimum safe fixed prefix from this data: 1 MiB** (max seen 906 KB; the largest IDR is not bounded by the format, so even 1 MiB
needs a checked fallback). NAL walk of every complete IDR: lengths tile the sample exactly and a VCL NAL is present, 0 failures.

### 2. Cost per chunk, session A (all 2398 chunks, one ffmpeg, 1440p60 HEVC → 320x180)
| Variant | Bytes read / chunk | Decode, steady state (mean / p95 / max) | feed-to-exit / chunk | ffmpeg CPU / chunk | 2 h total (2400 chunks) | Failed chunks |
|---|---|---|---|---|---|---|
| **D3D11VA, adaptive read** (cold cache) | **0.270 MB** | 2.17 / 2.66 / 8.8 ms | 2.33 ms | 3.4 ms | 5.6 s wall | 0 (+2 zero-filled) |
| D3D11VA, adaptive, warm cache | 0.270 MB | 2.03 / 2.46 / 9.1 ms | 2.22 ms | 3.1 ms | 5.3 s | 0 |
| D3D11VA, fixed 512 KiB | 0.499 MB (reads the full 512 KiB even when the IDR is small) | 2.17 / 2.65 / 24.7 ms | 2.34 ms | 3.1 ms | 5.3 s | **132** |
| D3D11VA, fixed 1 MiB | **0.998 MB** | 3.10 / 3.59 / 5.2 ms | 3.26 ms | 3.2 ms | 7.8 s | 0 |
| CUDA (NVDEC), adaptive | 0.270 MB | 1.79 / 2.43 / 3.5 ms | 1.91 ms | 2.9 ms | 4.6 s | 0 |
| CUDA + `scale_cuda`, adaptive | 0.270 MB | 1.57 / 2.50 / 3.6 ms | 3.97 ms | 3.2 ms | 9.5 s (first frame after 5.7 s of CUDA filter start-up) | 0 |
| CPU, all threads | 0.270 MB | 4.87 mean wall (bursty, p95 24 ms) | 4.97 ms | **61.7 ms** (148 CPU-s in total) | 11.9 s wall, 148 CPU-s | 0 |
| CPU, `-threads 1` | 0.270 MB | 45.6 / 74.0 / 244 ms | 45.65 ms | 45.3 ms | **109 s** | 0 |

Reading it: the GPU decode of a single IDR is ~2 ms, the same as the pipe/scale/copy cost, so GPU choice (D3D11VA vs CUDA) barely
matters; D3D11VA needs no CUDA runtime and is the better default. Reading the first fragment from disk takes p50 ≈ 1 ms (open+read
of ~0.3 MB), overlapped by the producer. Cold-cache vs warm-cache differ by < 0.2 ms/chunk here (local SSD); an SMB or HDD source
will move this and was not measured.
Wall time per chunk with ffmpeg start-up: first frame at ~0.35 s (D3D11VA), ~0.26 s (CUDA).

### 3. Bytes: fixed prefix vs adaptive (computed from the scan CSVs, mean bytes per chunk)
| Strategy | Session A | B | C | Correct for all chunks? |
|---|---|---|---|---|
| Fixed 512 KiB | 499 KB | 512 KB | 512 KB | no (5.5 to 26.5 % cut) |
| Fixed 1 MiB | 1024 KB | 1024 KB | 1024 KB | yes (max seen 906 KB) |
| **Adaptive: 4 KB, then exactly the fragment** | **277 KB** | **350 KB** | **352 KB** | yes (size comes from the `moof`), 2 reads |
| Hybrid: 256 KiB first read, 2nd read only if needed | 311 KB | 371 KB | 372 KB | yes; 2nd read for 46 / 65 / 69 % of chunks |
| Hybrid: 384 KiB first read | 401 KB | 438 KB | 434 KB | yes; 2nd read for 17 / 36 / 37 % |

The worst single chunk needs 0.82–0.88 MB, so "≤ 0.6 MB per chunk" holds as a mean, not as a per-chunk maximum.

### 4. Sprite sheets, 10x10 tiles, one thumbnail per 3 s chunk, session A (2 h = 24 sheets)
| Variant | Total (2 h) | Per hour of footage | Per thumbnail | Decode+encode wall (2 h) |
|---|---|---|---|---|
| JPEG 320 px, `-q:v 5` | 40.3 MB | 20.2 MB | 17.6 KB | 5.3 s |
| JPEG 320 px, `-q:v 10` | 24.6 MB | **12.3 MB** | 10.8 KB | 5.1 s |
| JPEG 240 px, `-q:v 8` | 18.2 MB | 9.1 MB | 7.9 KB | 5.2 s |
| JPEG 160 px, `-q:v 5` | 12.7 MB | 6.4 MB | 5.6 KB | 5.7 s |
| JPEG 160 px, `-q:v 10` | 7.9 MB | **3.9 MB** | 3.5 KB | 5.3 s |
| WebP 320 px, quality 60 | 29.5 MB | 14.8 MB | 12.9 KB | 14.0 s (CPU WebP encode) |
| WebP 320 px, quality 40 | 23.7 MB | 11.9 MB | 10.4 KB | 13.1 s |
| WebP 240 px, quality 50 | 17.3 MB | 8.7 MB | 7.6 KB | 8.5 s |
| WebP 160 px, quality 50 | 9.0 MB | 4.5 MB | 3.9 KB | 5.4 s |

One thumbnail per 6 s (every second chunk) halves each number. At 320 px, JPEG beats WebP on encode time and is similar in size, so
use JPEG unless size matters more than 8 s of background CPU per 2 h. A sampled 320 px sheet was viewed: colours correct (full
range preserved), frames in time order, no garbage tiles.

## Budget check (plan S4: "≤ 15 ms and ≤ 0.6 MB per chunk", and §5 "trickplay for a 2 h session < 40 s")
| Metric | Result | Budget | Pass |
|---|---|---|---|
| Decode per chunk, D3D11VA, steady state | 2.0–2.2 ms (p95 2.7 ms) | ≤ 15 ms | yes |
| Decode per chunk, CUDA | 1.8 ms | ≤ 15 ms | yes |
| CPU decode per chunk (info) | 4.9 ms wall on 16 threads, 45.6 ms on one thread | CPU option must exist | yes (works; 110 s/2 h on one core, 12 s on all) |
| Bytes read per chunk, adaptive | 0.27 MB mean (0.35 on other sessions), 0.88 max | ≤ 0.6 MB | yes as a mean |
| Bytes read per chunk, fixed 512 KiB | 0.5 MB, but 14.9 % of IDRs truncated | ≤ 0.6 MB with the whole IDR | **no** |
| Bytes read per chunk, fixed 1 MiB (zero failures) | 1.0 MB | ≤ 0.6 MB | **no** |
| 2 h session total (GPU) | 5.3–5.6 s | < 40 s | yes |
| Sprite sheets per hour | 3.9 MB (160 px) … 12.3 MB (320 px) | plan says the whole library download is "a few MB" | only at 160 px / 6 s spacing |

## Caveats and not measured
- Single machine, local SSD only. SMB latency matters for the adaptive read: two reads per chunk means two round trips; the 256 KiB
  hybrid needs one round trip for ~35–55 % of chunks.
- Not measured: running next to a playing mpv (GPU contention), Intel/AMD decoders, H.264 sessions, CPU use while the game is
  recording. Only HEVC was seen; all four sessions use the same NAL layout.
- The thumbnail-to-time mapping must use the chunk number (`tfdt`) of each fed fragment, not the position in the sheet: chunks can be
  missing or unreadable (two zero-filled chunks at the end of session A were skipped), and one frame comes out per chunk.
- ffmpeg prints "deprecated pixel format used" for `yuvj420p`. It is harmless here (`format=yuvj420p` is set explicitly).
- The maximum IDR size is not bounded by the format. The implementation must verify (`mdat` size vs bytes read, NAL walk) and
  fall back to a larger read instead of trusting a constant.

## Recommendation for Phase 4 (trickplay generator)
1. Read the first 4 KB (or 256 KiB), parse the `moof`/`mdat` header, then read exactly the rest of the first fragment (cap ~4 MiB).
2. Feed `init-stream0.m4s` + each first fragment to a single `ffmpeg -hwaccel d3d11va` (CPU fallback: same command without
   `-hwaccel`), output 10x10 JPEG sheets, 160–240 px wide.
3. Plan wording to change: replace "first 512 KB of each chunk" by "first fragment (moof + IDR) of each chunk", and "≤ 0.6 MB per
   chunk" stays valid as a mean.

Verdict: PARTIAL
