# S8 `android-detect`: Kotlin port of FastEncoder's AudioSync

Given FastEncoder's synthetic "sound 300 ms late" clip, when the Kotlin `AudioSync` port runs on the phone, then it reports
what the C# detector reports (+277 ms on this clip), with High confidence, and analyses 75 s in < 30 s. (Criterion set by the
maintainer 2026-11-07: the reference is the C# result, not the nominal 300 ms.)

## What is here
| Path | What |
|---|---|
| `app/src/main/kotlin/spp/spike/s8/AudioSync.kt` | line-by-line port of `AudioSync.cs` (analysis + per-frame video features). Pure Kotlin, no Android types |
| `app/src/main/kotlin/spp/spike/s8/MediaDecode.kt` | Android decode substrate: `MediaExtractor` + `MediaCodec` (replaces the two ffmpeg processes) |
| `app/src/main/kotlin/spp/spike/s8/MainActivity.kt` | adb-driven runner, writes `files/results/*.json` |
| `app/src/test/.../AudioSyncGoldenTest.kt` | JVM check: the port on the exact ffmpeg output the C# detector consumes vs the C# result |
| `golden-cs/` | tiny .NET console that links FastEncoder's `AudioSync.cs` and writes `golden.json` |
| `tools/make-fixtures.sh` | FastEncoder's `SelfTest.cs` recipe at 10, 14 and 75 s |
| `tools/dump-inputs.sh` | dumps `audio.f32`, `video.gray`, `video.pts` with the C# detector's own ffmpeg arguments |

Porting notes that matter for bit-exact results: C# `Math.Round` is banker's rounding (`Math.rint`), `x * x` stays `float`
before widening, `Complex.Abs` is .NET's scaled hypot (reproduced), and the FFT keeps C#'s twiddle recurrence. The only
deliberate change: `Prep(Shift(af, s))` is computed once per (audio feature, shift) instead of once per pair (same values).
FFT: the 30-line radix-2 is ported, no dependency.

## Results so far

### 1. Port vs C# on identical inputs (JVM, `testDebugUnitTest`) - PASS
Inputs dumped with ffmpeg 9.0 using the C# detector's arguments; expected values from `golden-cs` (FastEncoder `AudioSync.DetectAsync`).

| fixture | C# | Kotlin | analysis time (JVM, low-end 4-thread laptop CPU) |
|---|---|---|---|
| 10 s | +276.595 ms, High 12/12 | +276.595 ms, High 12/12 | 266 ms (first run, JIT warm-up) |
| 14 s | +276.601 ms, High 12/12 | +276.601 ms, High 12/12 | 181 ms |
| 75 s | +277.413 ms, High 12/12 | +277.413 ms, High 12/12 | 1159 ms |

Agreement is within the test tolerance of 1 µs and the confidence, agreeing and consistent counts are identical. C#'s own wall
time on a desktop for the 75 s file (decode + analysis) is 5.2 s.

**Criterion conflict (decided 2026-11-07: matches C#, +277 ms):** both implementations read **+277 ms** for a true +300 ms, so "300 ± 10 ms"
and "matches C#" cannot both hold for a faithful port. The bias belongs to the detector (S6 saw the same), and FastEncoder's own
self-test accepts `|AudioLate − 0.3| < 0.035`. Proposal: S8 passes on "matches C# within 1 ms + High confidence", and the 300 ±
35 ms window from FastEncoder's self-test.

### 2. On the phone (test phone, Android 13, 75 s fixture, 3 runs) - PASS
| mode | result | time per 75 s |
|---|---|---|
| `raw` (the dumped ffmpeg inputs, analysis on ART) | **+277.413 ms**, High 12/12 (= C#) | analysis 2.1 s |
| `file` (MediaExtractor + MediaCodec decode, then analysis) | **+276.608 ms**, High 12/12, identical over 3 runs | 8.0 - 10.8 s total (decode 5.8 - 8.5 s, analysis 2.1 - 2.3 s) |

Decoders: `c2.android.aac.decoder` (audio), `OMX.qcom.video.decoder.avc` (video). The Android decode path lands 0.8 ms from
the C# result (different AAC decoder, resampler and downscale), well inside FastEncoder's ±35 ms. Time budget: 30 s, used ≤ 11 s.
Bug found on the device and fixed: `MediaCodec.getName()` was read after `release()` (throws `IllegalStateException`).

## Build
```bash
ANDROID_ROOT=<dir> bash scripts/android/bootstrap.sh       # SDK + Gradle 9.9 into <dir>, needs JDK 17+
export ANDROID_HOME=<dir>/sdk
bash tools/make-fixtures.sh <fixtures> 10 14 75
bash tools/dump-inputs.sh <fixtures>/sync_late300_75s.mp4 <golden>/late300_75s 0 75
dotnet run --project golden-cs -c Release -p:FastEncoderDir=<FastEncoder checkout>/ -- <fixtures>/sync_late300_75s.mp4 75 <golden>/late300_75s
S8_GOLDEN=<golden> <dir>/gradle-9.9.0/bin/gradle testDebugUnitTest assembleDebug
```

## Run on the device
```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
F=/sdcard/Android/data/spp.spike.s8/files
adb shell mkdir -p $F
adb push <fixtures>/sync_late300_75s.mp4 $F/
adb push <golden>/late300_75s $F/
adb shell am start -S -n spp.spike.s8/.MainActivity --es file sync_late300_75s.mp4 --ei repeat 3
adb shell am start -S -n spp.spike.s8/.MainActivity --es raw late300_75s --ei repeat 3
adb pull $F/results .
```

Verdict: **VALIDATED** on the maintainer's criterion "matches C# (+277 ms) + High + < 30 s".
