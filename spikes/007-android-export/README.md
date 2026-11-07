# S7 `android-export`: lossless range + audio shift, and a Transformer re-encode

Given a 30 s downloaded range, when the Media3 Muxer builds the lossless file with a +300 ms audio shift and Transformer
re-encodes at 2×, then both are in sync and full-range colour survives.

## Design (from the S7 research)
- **A) Lossless** (`mode remux`): `MediaExtractor` over the downloaded fragmented MP4 (`init-stream0.m4s` + chunks, and the same
  for audio) → Media3 `Mp4Muxer` 1.11.1. Video samples are written **in decode order with their original PTS** (Steam's video has
  B-frames; `BufferInfo` has no DTS and the muxer derives `ctts` from the PTS order, so the app must never sort or "fix" PTS).
  The audio shift is a muxer-level PTS offset (+300 000 µs on every audio sample): Media3 has no audio-offset API and a gap would
  force an audio re-encode. The muxer turns the offset into an edit list. `--ez color_full true` attaches
  `ColorInfo(BT.709, FULL, SDR)` so an `nclx` box is written (Steam signals full range only in the HEVC SPS VUI, Media3 sees
  `colorInfo == null`).
- **B) Re-encode** (`mode transcode`): Media3 Transformer, HEVC (`OMX.qcom` HW encoder on the test phone), optional speed via
  `EditedMediaItem.Builder.setSpeed`. Its input is A's output, so with `color_full` the encoder gets the range from the `colr`
  box.
- `mode codecs`: runtime `MediaCodecList` inventory, including whether an **AV1 encoder** exists (the ROM has a software AV1
  decoder, so presets must gate on the encoder).

## Results (test phone, Android 13; 30 s range of a real 1440p60 HEVC session, chunks 1-10)
| check | result |
|---|---|
| lossless remux, 30 s | 1.6 s on the phone; 1801 video + 1404 audio samples |
| bit-exactness | video elementary stream MD5 of the export == source (`ffmpeg -c copy -f hash`) |
| B-frames / order | `has_b_frames=2` kept; 1191 PTS decreases written in decode order |
| audio shift | audio `start_time` 0.300 s (edit list), video 0.000 |
| sync (FastEncoder AudioSync, 14 s synthetic clip) | source +276.6 ms → export +576.6 ms = **+300.0 ms**, High 12/12 |
| full range | export: `color_range=pc`, BT.709 primaries/matrix/transfer. Media3 on this phone already reads full range from the source, so `color_full` was not needed here (keep it as a guard) |
| Transformer 2× | HW `OMX.qcom.video.encoder.hevc` at 2560x1440, 1801 frames → 15.0 s, 10.9 s wall = **2.8× realtime**; AAC `c2.android.aac.encoder` |
| **2× colour: root cause** | Not Media3: the Transformer already asks the encoder for BT.709 full (`color-standard=1`, `color-range=1`). Two device issues: (a) the Qualcomm HW HEVC decoder → Surface path treats the full-range input as limited range whatever range it is given, and stretches and clips luma/chroma before the GL pipeline. Software `c2.android.hevc.decoder` honours the range. (b) The Qualcomm HW encoders (HEVC and AVC) ignore the requested colour standard and always write BT.601 full; the 601 tag matches the pixels they write. The software encoders refuse 1440p |
| **2× colour: fix** | `--es dec auto` (default) uses the software decoder for full-range input. Pixels checked on the PC against the source: RGB PSNR **28.8–30.3 dB before, 37.8–39.1 dB after**, mean RGB within 0.5. Output is still tagged BT.601 full range, which is correct for its pixels. Cost: **1.83× realtime** (software decode) instead of 2.8× |
| 2× sync | 14 s synthetic clip, FastEncoder detector, High 12/12: source +276.6 ms → remux +576.6 ms (+300.0) → 2× +291.7 ms (expected 288.3, error 3.4 ms) |
| Transformer 1× | no edit → Transformer passes the file through (same size, no encoder): fine for us, it means "lossless" exports should not go through Transformer anyway |
| AV1 | **no AV1 encoder** on this phone (`findEncoderForFormat` = null); decoder `c2.android.av1-dav1d.decoder` (software). AV1 export presets must be gated at runtime |

## Device runbook (phone on USB)
```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
F=/sdcard/Android/data/spp.spike.s7/files; adb shell mkdir -p $F/range
# a 30 s range = 10 chunks of one session, concatenated the way the phone stores them
cat init-stream0.m4s chunk-stream0-000{01..10}.m4s > v.mp4; cat init-stream1.m4s chunk-stream1-000{01..10}.m4s > a.mp4
adb push v.mp4 a.mp4 $F/range/
adb shell am start -S -n spp.spike.s7/.MainActivity --es mode codecs
adb shell am start -S -n spp.spike.s7/.MainActivity --es mode remux --es video range/v.mp4 --es audio range/a.mp4 --es out out/lossless.mp4 --ei shift_ms 300
adb shell am start -S -n spp.spike.s7/.MainActivity --es mode remux --es video range/v.mp4 --es audio range/a.mp4 --es out out/lossless-full.mp4 --ei shift_ms 300 --ez color_full true
adb shell am start -S -n spp.spike.s7/.MainActivity --es mode transcode --es in out/lossless-full.mp4 --es out out/x2.mp4 --ef speed 2
# sync check on the synthetic "sound 300 ms late" clip (S8 fixture): detector reads +277 before; expect about +577 after +300,
# and about half of that after a 2x speed-up
adb push sync_late300_14s.mp4 $F/
adb shell am start -S -n spp.spike.s7/.MainActivity --es mode remux --es video sync_late300_14s.mp4 --es out out/sync-shift.mp4 --ei shift_ms 300
adb pull $F/results; adb pull $F/out
```
Checks on the desktop: `ffprobe -show_streams` (start_time, color_range, has_b_frames), a box dump for `elst`/`ctts`/`colr`,
`ffmpeg -vf signalstats` Y min/max of source vs export for range, FastEncoder's AudioSync (S8 `golden-cs`) on the sync exports,
packet-level compare of source vs lossless video (`ffprobe -show_packets` sizes + PTS).

**Ambiguity to confirm:** "re-encodes at 2×" is read as a 2× speed-up export (requirement 7). The realtime factor of the encode
is recorded too (`realtime_factor_vs_input`), since the research found HEVC HW encode at about 1.5× realtime for 1080p60 and
about 1.1× for 1440p60.

For the product: detect the decoder range bug at runtime (decode one known frame) rather than by codec name, and expect BT.601-tagged
re-encodes on phones whose encoders ignore the colour request. Only one phone tested.

Verdict: **PASS** for the lossless path (re-run with the new build: bit-exact, +300 ms exact, full range kept). **PARTIAL** for the 2×
re-encode: colours and sync are correct, but the output is BT.601-tagged (encoder limit), and a full-range source needs the software
decoder (1.83× instead of 2.8× realtime).
