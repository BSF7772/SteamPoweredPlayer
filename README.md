# SteamPoweredPlayer

Native Windows 11 app (C# · WinUI 3 · Mica · libmpv) that plays and edits your **Steam Game Recordings** straight from disk, with
or without Steam running. An Android companion (Kotlin · Compose · libmpv) reads the same library over encrypted SMB 3.1.1.
Sister project of [FastEncoder](#fastencoder): same look, same presets, same audio-sync detector.

> **Status: plan phase. Nothing is built yet.** Everything below is the target. The plan, with the evidence behind it, is in
> [`.hermes/plans/`](.hermes/plans/), and the reverse-engineered recording format is in
> [`docs/steam-recording-format.md`](docs/steam-recording-format.md).

## Run
Double-click `SteamPoweredPlayer.bat`. The first run builds the app; after that it starts instantly.
Use `SteamPoweredPlayer.bat --rebuild` after editing the source.

Requires the .NET 10 SDK and ffmpeg on PATH (`winget install Microsoft.DotNet.SDK.10 Gyan.FFmpeg`). `tools\bootstrap.ps1` downloads
everything else (libmpv, and for Android the JDK, SDK and Gradle) into `.tools\` inside the repo, so nothing is installed system-wide.

On first run the app finds Steam's recording folder in Steam's own settings, and proposes a data folder (index, art, thumbnails)
next to it, which you confirm or change. You choose an export folder the first time you export.

### FastEncoder
FastEncoder is a git submodule (`external/FastEncoder`), and its `src/Core` files are compiled in, not copied. Clone with
`git clone --recursive`, or run `git submodule update --init` afterwards. To build against another checkout, pass
`-p:FastEncoderDir=<path>`.

## Features
- **Library by game**: Steam and non-Steam games alike (shortcuts get their real names and art), plus storage per game. Each game shows:
  - **Cache**: Steam's background recording, the last 2 h (or whatever you set in Steam), marked temporary. It grows while you
    play and Steam trims it as it goes. Replay it, and **Save clip** to keep a part before Steam drops it.
  - **Clips**: saved and downloaded clips. **Recordings**: on-demand ones. **Exports**: files in your export folder.
- **Player**: GPU decoding (D3D11VA, zero-copy) or CPU. Opens and seeks instantly, even 2-hour sessions. Speed 0.25–4× with
  pitch correction, frame step, A-B loop, resume, full-resolution screenshots.
- **Robust**: plays recordings Steam itself can't open, such as broken or unfinished manifests, sessions Steam is trimming while
  you watch, and gaps.
- **Steam timeline**: game-mode colour bands, event markers (kills, deaths, rounds, achievements, screenshots), map/mode chips,
  next/previous event, filmstrip preview when hovering the timeline. Search events across the whole library.
- **Edits, like FastEncoder**: several cuts per recording, speed, volume, remove audio, an audio-sync slider you hear live while
  dragging, **Detect** (automatic sync), fix timing drift.
- **Export with FastEncoder's presets**: Balanced / Archive / Pristine (SVT-AV1), GPU Turbo (NVENC/QSV/AMF), Compact 720p,
  Universal (H.264), Fit to size, and Trim only (lossless, on Steam's 3-second keyframes). "Open in FastEncoder" hands a cut to the full app.
- **Highlight reel**: chosen events (±N seconds) stitched into one video.
- **Network libraries**: open a library on another PC. Wired connections stream in real time. Wi-Fi downloads first, so dropped
  packets never stall playback.
- **Delete with care**: off until you enable it, clips only (never Steam's cache), Recycle Bin by default, and every delete confirmed.

Keys: `Space` play/pause · `I` / `O` set start/end · `R` reset trim · `← →` ±5 s · `, .` frame step · `[ ]` previous/next event ·
`J K L` slower/pause/faster · `F` fullscreen · `Ctrl+E` export · `Ctrl+F` search

## Android
- **Server profiles**, as many as you like per library, e.g. one for home LAN and one for Tailscale or another VPN. The app uses
  whichever is reachable, shows which one, and you can switch.
- **Secure and read-only**: SMB 3.0–3.1.1 with encryption required (AES-256-GCM) and signing. The app never writes to the share,
  and passwords are protected by the Android Keystore.
- **Download-first**: on Wi-Fi and mobile data, recordings (or just the part you want) download chunk by chunk in the background
  and resume after any drop. Playback reads local files, so the network never causes a stutter. Wired (USB-Ethernet) connections can
  stream directly.
- **Same editing as the desktop**: cuts, speed, volume, audio sync + Detect. Exports run **on the phone** (Media3; hardware
  HEVC/H.264, or lossless trim) and save to `Movies/SteamPoweredPlayer`.

## Layout
- `src/Core/` has no UI code: `Steam/` format + config readers, `Library/` index + thumbnails, `Playback/` libmpv binding + D3D11
  renderer, `Export/` lossless range + export + delete service, `Net/` remote libraries + downloads
- `src/FastEncoder/` (linked): `Ff.cs`, `Encoders.cs`, `Presets.cs`, `Pipeline.cs`, `AudioSync.cs`
- `src/MainWindow.xaml(.cs)`: UI · `android/`: phone app · `tests/`: xUnit + shared golden fixtures · `docs/` · `tools/`
- Settings: `%LOCALAPPDATA%\SteamPoweredPlayer\settings.json`. Saved passwords go to Windows Credential Manager.

## Self-test
`SteamPoweredPlayer.exe --selftest report.txt` builds Steam-style recordings with ffmpeg (normal, trimmed, broken manifest, gaps,
still recording, crashed) and checks scanning, playback, seeking, a Steam-style trim during playback, interrupted downloads, every
export preset, and audio sync (a clip with sound 300 ms late must be detected and come out in sync). Exit code 0 means everything passed.

`SteamPoweredPlayer.exe --bench report.txt` measures open, first-frame and seek times against your library.

## Docs
- [Steam recording format](docs/steam-recording-format.md): layout, manifests, chunks, timelines, game ids
- [SMB security](docs/smb-security.md): enabling SMB encryption on Windows and troubleshooting clients that get "access denied"
- [AGENTS.md](AGENTS.md): instructions for AI coding agents

## License
[GPL-3.0-or-later](LICENSE). The libmpv and FFmpeg builds this app ships are GPL. FastEncoder, which is compiled in, is MIT.
