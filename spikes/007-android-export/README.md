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

## Status
The APK builds (AGP 9.5.1, Media3 1.11.1, compileSdk 36, minSdk 29). Nothing has run on the phone yet.

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

Verdict: PENDING (built, device run not done)
