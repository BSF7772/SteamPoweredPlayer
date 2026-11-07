# S8 `android-detect`: Kotlin port of FastEncoder's AudioSync

Given FastEncoder's synthetic "sound 300 ms late" clip, when the Kotlin `AudioSync` port runs on the phone, then it reports
300 ± 10 ms, matches C#, and analyses 75 s in < 30 s.

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

**Criterion conflict (needs a maintainer decision):** both implementations read **+277 ms** for a true +300 ms, so "300 ± 10 ms"
and "matches C#" cannot both hold for a faithful port. The bias belongs to the detector (S6 saw the same), and FastEncoder's own
self-test accepts `|AudioLate − 0.3| < 0.035`. Proposal: S8 passes on "matches C# within 1 ms + High confidence", and the 300 ±
35 ms window from FastEncoder's self-test.

### 2. On the phone (MediaCodec decode + analysis, 75 s budget) - NOT RUN YET
The APK builds (AGP 9.5.1, compileSdk 36, minSdk 29). The device run needs the phone on USB; see "Run on the device".
Expected differences: the decode substrate differs (framework AAC decoder, our resampler, box-downscale of the luma plane), so
the device result is compared with C# within a tolerance, not bit for bit. The `raw` mode runs the analysis on the phone over the
same dumped inputs, which separates "ART gives the same numbers" from "MediaCodec decodes the same samples".

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

Verdict: PENDING (port matches C# exactly on the JVM; the phone run and the 300 ± 10 ms vs "matches C#" decision are open)
