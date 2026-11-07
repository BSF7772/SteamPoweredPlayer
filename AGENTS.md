# AGENTS.md

Guidance for AI coding agents (Codex, Copilot, Claude Code, Cursor, Hermes, …) working in this repository.
Humans: start with [README.md](README.md). Format: [agents.md](https://agents.md).

## Project overview
SteamPoweredPlayer is a fast, native Windows player and editor for **Steam Game Recordings**: background recordings and clips.
It works without Steam running, and an Android companion reads the same library over encrypted SMB.
It's a sister project of **FastEncoder**, and reuses FastEncoder's encoding pipeline, presets and audio-sync detector.

Requirements every change must serve:
1. Efficient open-source codecs (ffmpeg: SVT-AV1, x264/x265, Opus; libmpv for playback).
2. GPU acceleration (NVENC/QSV/AMF encode, D3D11VA/MediaCodec decode) with a CPU option everywhere.
3. Integration with FastEncoder: link its Core, don't fork it.
4. FastEncoder's design language: Mica, cards, brand gradient, emoji preset tiles, staggered animations.
5. Every recording listed by game, Steam and non-Steam alike.
6. Steam-player parity (timeline, markers, replay, clip, export), but better, and with no dependency on Steam.
7. Cuts, auto sync and speed-up, exactly like FastEncoder.
8. Android companion: secure SMB (3.1.1, encryption), read-only access to the library, editing and exporting on the device.

**Status: Phase 1 (Core library, no UI).** The approved plan lives in `.hermes/plans/`; don't start a phase the maintainer hasn't
approved. Spikes (`spikes/NNN-name/`) are throwaway and must end with a `Verdict:` line.

## Commands
Targets, available as each phase lands. Run everything from the repo root in PowerShell.
```powershell
tools\bootstrap.ps1                      # portable libmpv, JDK, Android SDK, Gradle into .tools\ (pinned, SHA-256 checked)
. tools\env.ps1                          # sets JAVA_HOME / ANDROID_HOME / GRADLE_USER_HOME / NUGET_PACKAGES for this shell only
dotnet build src -c Release              # desktop app
dotnet test tests                        # unit tests (fast, no real library needed)
SteamPoweredPlayer.bat --rebuild         # build + run
SteamPoweredPlayer.exe --selftest out.txt   # synthetic end-to-end check, exit code 0 = pass
SteamPoweredPlayer.exe --bench out.txt      # performance budgets on a real library
cd android; .\gradlew testDebugUnitTest assembleDebug   # phone app
```
Opt-in tests against a real library are read-only and need `SPP_REAL_LIBRARY=<path to a BackgroundRecordPath>`.

## Tech stack
- Desktop: C# 14 / .NET 10, WinUI 3 (Windows App SDK 1.8, unpackaged), libmpv ≥ 0.42 (D3D11 render API, `hwdec=d3d11va`) via
  `LibraryImport`, Vortice.Direct3D11, Google.Protobuf, ValveKeyValue, ffmpeg ≥ 8 on PATH, xUnit.
- FastEncoder (MIT, git submodule `external/FastEncoder`, pinned): `Ff.cs`, `Encoders.cs`, `Presets.cs`, `Pipeline.cs`,
  `AudioSync.cs` compiled in via `<Compile Include … Link>` from `$(FastEncoderDir)`. Clone with `--recursive`.
- Licence: GPL-3.0-or-later (see `LICENSE`).
- Android: Kotlin 2.x, Jetpack Compose (Material 3), libmpv AAR (`hwdec=mediacodec`), codelibs JCIFS 3.x (SMB 3.1.1),
  Media3 Transformer + Muxer, WorkManager, kotlinx.serialization, Coil.

## Project structure
```
src/Core/            SteamPoweredPlayer.Core.csproj (all of Core + the linked FastEncoder sources; tests reference only this)
src/Core/Steam/      Steam config, folder names, game ids, VDF, protobuf (.pb), timelines: pure parsing, no I/O side effects
src/Core/Library/    scanner (event-driven), index, data folder, artwork, trickplay
src/Core/Playback/   libmpv binding, D3D11 renderer, EDL builder, cache follower
src/Core/Export/     lossless range source, export queue, delete service (the only code allowed to delete)
src/Core/Net/        remote-library access, link-type detection, chunk downloader
external/FastEncoder/ git submodule (never edit it from this repo; change FastEncoder upstream, then bump the pin)
src/                 MainWindow.xaml(.cs) and the views
android/             phone app (Gradle), mirrors Core: smb/ data/ play/ edit/ export/ ui/
tests/               SteamPoweredPlayer.Tests.csproj, xUnit tests mirroring src/Core folders, tests/fixtures (synthetic), tests/fixtures/golden (shared with Android tests)
docs/                format notes (steam-recording-format.md), SMB security, bench results
spikes/              throwaway experiments (Phase 0)
.hermes/plans/       the plan
```

## Domain rules (read docs/steam-recording-format.md first)
- **Never trust `session.mpd`.** It can be stale, wrong or all zero bytes. Build playback and export from the chunk files actually on
  disk (`init-stream{0,1}.m4s` + `chunk-stream{0,1}-NNNNN.m4s`, 3 s each).
- **Background recordings are Steam's rolling cache.** Steam trims them continuously. Open every file with read + write + **delete**
  sharing so Steam's cleanup never fails, and survive files vanishing mid-read.
- Folder and timeline timestamps are **UTC**.
- Lossless cuts snap to the 3 s keyframe grid; frame-exact cuts re-encode.
- Wireless links are unreliable: **download-first** on wireless, stream only on wired links (auto-detected, user override).
- No polling: use FileSystemWatcher, SMB change notify, libmpv events and Android network callbacks.

## Code style
- Every source file starts with `// SPDX-License-Identifier: GPL-3.0-or-later` (Kotlin too; `#` comment in scripts).
- C#: nullable enabled, file-scoped namespaces, `sealed record` for data, `async`/`await` with `CancellationToken` everywhere
  I/O happens, invariant culture for every number written to ffmpeg, mpv or JSON. No UI code in `src/Core`.
- Kotlin: coroutines + Flow, no blocking I/O on the main thread, Compose state hoisted to ViewModels.
- Small, pure, testable functions in Core:
```csharp
public static RecordingName? ParseRecording(string folderName)
{
    var m = RecordingRegex().Match(folderName);              // ^(bg|clip)_(\d+)_(\d{8})_(\d{6})$
    if (!m.Success) return null;                             // unknown layouts are reported, never guessed
    var utc = DateTimeOffset.ParseExact(m.Groups[3].Value + m.Groups[4].Value, "yyyyMMddHHmmss",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    return new(m.Groups[1].Value == "bg" ? RecordingKind.CacheSession : RecordingKind.Clip,
               ulong.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), utc);
}
```

## Testing
- TDD for Core and the Android data layer: write the failing test, make it pass, refactor.
- Fixtures are **synthetic** (generated with ffmpeg's dash muxer using Steam's naming) or tiny hand-made `.pb`/`.json`/`.vdf`
  samples. Never commit real recordings or anything taken from a real user's library.
- Golden files in `tests/fixtures/golden/` must produce identical results in the C# and Kotlin parsers.
- Performance budgets live in the plan; `--bench` fails when one is missed. Don't relax a budget without the maintainer's OK.
- Never delete or skip a failing test to get green; fix the cause or ask.

## Git workflow
- Branch `main`. Small commits with conventional messages: `feat(scanner): …`, `fix(edl): …`, `docs: …`, `test: …`, `spike: …`.
- Commit and push after every coherent update. Run `dotnet test` (and Gradle unit tests when Android changed) before committing.
- Never commit build output, `.tools/`, media files (`*.mp4 *.m4s *.m4a *.mkv`), keystores, `local.properties` or `.env`.

## Boundaries
- ✅ **Always:** keep paths configurable (read Steam's settings, never hardcode them), open Steam files read-only with delete-sharing,
  keep the phone's access to the share read-only, put tools under `.tools/`, update docs when behaviour changes.
- ⚠️ **Ask first:** changing anything in FastEncoder, adding a dependency, changing the index/data-folder format, relaxing a
  performance budget, anything that writes or deletes inside Steam's recording folder, any history rewrite or force-push.
- 🚫 **Never:** commit secrets, credentials or personal data (real paths, hostnames, IPs, account names, user ids, game lists,
  hardware details from a developer's machine); log passwords; store passwords unencrypted (Android: Keystore; Windows:
  Credential Manager/DPAPI); delete files outside `DeleteService`; delete Steam's cache sessions; install anything system-wide;
  bypass git hooks with `--no-verify`.

## Security
- SMB: dialect 3.0–3.1.1 only, encryption required by default (AES-256-GCM preferred), signing on, read-only opens. An "allow
  unencrypted" switch exists per server profile, off by default, and the UI warns when it's used.
- The delete feature is off by default, uses the Recycle Bin when possible, validates every path, and asks for explicit
  confirmation (typed for bulk). See the plan, §4.5.
