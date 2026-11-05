# SteamPoweredPlayer Implementation Plan

> **For agents:** plan phase. Read `AGENTS.md` first. Phase 0 (spikes) comes before any product code, and each later phase
> starts only after the maintainer approves it. Build task by task with TDD, and push after every update.

**Goal:** A very fast native player and editor for Steam Game Recordings that lists everything by game, including Steam's
temporary background cache. It plays and edits without Steam and exports through FastEncoder. An Android companion reads the
same library read-only over encrypted SMB, works **download-first** on unreliable wireless links, and edits and exports on the phone.

**Architecture:** One engine idea everywhere: **libmpv** plays an **mpv EDL** built from the chunk files actually present; Steam's
manifest is never trusted. On Windows, libmpv's **D3D11 render API** draws into a WinUI 3 `SwapChainPanel`, so FastEncoder-style
XAML overlays the video. Exports first cut the chunk range losslessly, then run FastEncoder's `Pipeline` when a re-encode is
needed. Remote clients pick by link type: **wired → stream; wireless → download chunks first, play local files**.

**Tech stack:** see `AGENTS.md` → Tech stack. Format facts: `docs/steam-recording-format.md`. SMB setup: `docs/smb-security.md`.

---

## 0. Revision history
| Rev | Change |
|---|---|
| 1 | Initial plan |
| 2 | Background recording modelled as Steam's rolling cache; UTC names; SMB encryption guidance; program-managed data folder; workspace-only tooling; server profiles; read-only phone access; export on the phone |
| 3.1 | Licence decided: **GPL-3.0-or-later** (§2 D19). FastEncoder will be published (MIT) and consumed as a **git submodule** (D5). Linux clients are **out of scope** (the CLI idea was dropped). Repository recreated with a clean history |
| 3 | **Repo is treated as public:** all machine/user specifics removed (they live in the maintainer's local notes); AGENTS.md rewritten to the agents.md conventions; format notes moved to `docs/`. **Download-first** on wireless links, streaming only on wired (§4.3). **Export folder chosen by the user** (no default). **Delete feature with safety rails** (§4.5). SMB "access denied" root cause and options (`docs/smb-security.md`). Android `minSdk 29`. A cross-platform CLI for Linux clients (question in §9) |

---

## 1. Findings that drive the design
Measured on a development machine (NVMe SSD, desktop NVIDIA GPU) against a real library of about 200 recordings. Details are in
`docs/steam-recording-format.md`.

| Finding | Consequence |
|---|---|
| Recordings are MPEG-DASH: init segments + independent 3 s fMP4 chunks per track, HEVC (or H.264) + AAC, full-range video, one IDR per chunk | Lossless cuts on a 3 s grid; chunks are natural units for download, resume and caching |
| `session.mpd` is often stale (wrong `startNumber`, zero duration), all zeros after a crash, or `dynamic` while recording. ffmpeg can't open those, and a rewritten MPD still fails on seeks | Index the chunk files themselves and play through an mpv EDL |
| `init + chunk_a … chunk_b` byte-concatenated is a valid fMP4. A 30 s lossless remux takes ~0.35 s; a 30 s cut 1 h into a broken 2 h session takes 3.7 s and comes out in sync | Fast lossless export without the manifest |
| HEVC 1440p60 hardware decode runs ~6× realtime | Trickplay/thumbnails on the GPU; playback CPU stays tiny |
| Background recording is a **per-game rolling cache** (`BackgroundMaxKeep`, 2 h default, up to 24 h), trimmed continuously by Steam | Separate "Cache · temporary" view; follow trims and growth live; never block Steam's deletes |
| Folder and timeline timestamps are UTC | Parse with `AssumeUniversal`; show local time |
| Non-Steam games use `gameid = (shortcutAppId << 32) \| 0x02000000` | Names and art from `shortcuts.vdf` and `config/grid` |
| Some Android SMB clients don't negotiate SMB 3 encryption; an encrypted share then returns "access denied" (server event 1046) | Our client must support encryption; troubleshooting in `docs/smb-security.md` |
| Wireless links between phone and PC can be unreliable (heavy packet loss) | Download-first design; streaming only on wired links |

## 2. Decisions
| # | Decision | Chosen | Alternatives | Why |
|---|---|---|---|---|
| D1 | Desktop UI | C# 14, .NET 10, WinUI 3, Mica, unpackaged | Rust+egui, C++/WinRT, Avalonia, Flutter | FastEncoder's look and toolchain, links its Core, native |
| D2 | Desktop playback | libmpv ≥ 0.42 D3D11 render API → `SwapChainPanel`, `hwdec=d3d11va` (zero-copy), `hwdec=no` for the CPU option | `wid` child HWND, FFmpegInteropX, LibVLC | Live `audio-delay`, pitch-correct speed, frame-step, `hr-seek`, A-B loop, EDL built in; XAML overlays. `wid` is the fallback |
| D3 | Opening recordings | Own chunk index → EDL (`!mp4_dash,init=` + `!new_stream`) | MPD via ffmpeg/mpv | MPD fails on real sessions. Fallback: libmpv `stream_cb` virtual fMP4 |
| D4 | Export | Stage 1 lossless range (byte-concat → `ffmpeg -c copy`); stage 2 FastEncoder `Pipeline` on that file | Re-encode from the MPD | Fast, robust, reuses every preset/sync/CFR fix |
| D5 | FastEncoder | **Git submodule `external/FastEncoder`** (pinned commit); its Core sources are linked into the build (`$(FastEncoderDir)`, defaulting to the submodule; overridable to a side-by-side checkout for joint development); "Open in FastEncoder" hand-off | Fork / copy / NuGet package | One source of truth, reproducible builds for anyone cloning with `--recursive` |
| D6 | Encoders | ffmpeg: SVT-AV1 10-bit + Opus (CPU), NVENC/QSV/AMF (GPU, each proven by a test encode), x264/x265 | — | FastEncoder's catalog |
| D7 | Android UI | Kotlin + Compose (Material 3), FastEncoder palette and shapes | .NET Android, Flutter | Performance, battery, platform APIs |
| D8 | Android playback | libmpv AAR, `hwdec=mediacodec`, same EDL builder, **local files** in download-first mode | ExoPlayer | Same behaviour as the desktop; ExoPlayer is the fallback |
| D9 | Android SMB | JCIFS 3.x, SMB 3.0–3.1.1, encryption required by default, read-only opens with share read+write+delete, server profiles | smbj, libsmb2 | AES-256-GCM, change notify, maintained |
| D10 | Network mode | **Auto by link type**: wired → stream (loopback range server, read-ahead); wireless/VPN-over-wireless → **download-first** chunk cache. User override per profile | Always stream | Dropped packets on wireless must never reach the player |
| D11 | Shared state | Desktop is the only writer of the data folder; phones only read it and keep their own state locally | Phone sidecars on the share | Read-only phone access |
| D12 | Data folder | Proposed on first run next to `BackgroundRecordPath` (so remote clients see it through the same share), confirmed or changed by the user; never inside Steam's folder | Hardcoded path | Shared project: nothing hardcoded |
| D13 | Export folder | **Chosen by the user** on first export (folder picker), remembered, changeable in Settings. No default; Steam's export folder isn't assumed | Default to Steam's | Maintainer's decision |
| D14 | Delete | Off by default; clips and own exports only; Recycle Bin; path guard; typed confirmation for bulk (§4.5) | No delete | Needed for a shared project, done safely |
| D15 | Change detection | Events only: FileSystemWatcher, SMB2 CHANGE_NOTIFY, libmpv events, Android `NetworkCallback` | Polling | No polling |
| D16 | Secrets | Android Keystore (AES-GCM key, TEE-backed where available); Windows Credential Manager/DPAPI; never logged | Plain prefs | Common-sense security |
| D17 | Tooling | Portable in `.tools/` via `tools/bootstrap.ps1` (pinned + SHA-256); `tools/env.ps1` sets env for the current shell only | System installs | Nothing system-wide |
| D18 | Public-safety | No developer-machine data in the repo: placeholders in docs, synthetic fixtures, maintainers keep local pre-commit checks | — | The repo is shared |
| D19 | Licence | **GPL-3.0-or-later** for this repo; SPDX headers (`// SPDX-License-Identifier: GPL-3.0-or-later`) in every source file; `docs/third-party.md` lists bundled components and their licences when binaries ship | MIT/Apache with LGPL-only builds of libmpv + FFmpeg | The libmpv builds we ship are GPL: Windows builds (shinchiro) compile FFmpeg with `--enable-gpl --enable-version3` (x264/x265 linked), and the Android libmpv AAR builds FFmpeg with `--enable-gpl --enable-version3`, so the distributed app is GPLv3 regardless. GPLv3 (not v2) is needed for Apache-2.0 deps (Media3) and FFmpeg "version3" builds. Every other dependency (MIT, BSD, Apache-2.0, LGPL-2.1 JCIFS) is compatible. FastEncoder (MIT) can be compiled in. A permissive licence would only be honest with self-built LGPL libmpv/FFmpeg on both platforms, which costs maintenance for no user benefit |

## 3. Architecture
```
 Steam (read-only for us)                                   Data folder (desktop writes; remote clients read)
 <BackgroundRecordPath>/ video/bg_*  clips/clip_*  timelines   <user-chosen, default next to it>/SteamPoweredPlayer/
 <Steam>/userdata/<id>/config/localconfig.vdf (GameRecording)     library.json  library-id  art/  trick/  proxies/  edits/
 shortcuts.vdf · appcache/librarycache                                       ▲
 ┌──────────────── Windows: SteamPoweredPlayer.exe ─────────────────────────┼──────────────────────────────────────────┐
 │ Core/Steam    SteamConfig, FolderNames (UTC), GameIds, Vdf, RecordingPb, Timeline                                 │
 │ Core/Library  Scanner (FSW, cache-aware) → Index → IndexStore ───────────┘  Artwork · Trickplay · Proxies (opt.)   │
 │ Core/Playback Mpv (LibraryImport) · MpvD3D11Renderer → SwapChainPanel · Edl.Build · CacheFollower                  │
 │ Core/Export   RangeSource (share-delete opens) → ffmpeg -c copy → [FastEncoder Pipeline] · DeleteService           │
 │ Core/Net      LinkDetector (wired/wireless) · ChunkDownloader (resumable, prioritised) · LocalChunkCache (LRU)     │
 │ UI            Library (games · Cache strip · Clips · Recordings · Exports) │ Player (3-column) │ Downloads │ Settings│
 └────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
          ▲ SMB 3.1.1, encrypted, read-only · server profiles (LAN, VPN, …)
 ┌────────┴──────── Android companion ──────────────────────────────────────────────────────────────────────────────────┐
 │ smb/ profiles + Keystore, JCIFS pool, endpoint chooser · net/ LinkDetector, ChunkDownloader (WorkManager), cache     │
 │ data/ IndexRepository (library.json via change notify; fallback self-scan) · local edits/positions                   │
 │ play/ EdlBuilder → libmpv (local chunks; or loopback range server when wired) · edit/ AudioSyncKt                    │
 │ export/ RangeMuxer (Media3 Muxer) → Transformer (HW HEVC/H.264) → MediaStore · ui/ (Compose)                         │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Playback path:**
1. One directory listing per session. Segment N is complete when both `chunk-stream0-N` and `chunk-stream1-N` exist without
   `.tmp`. Complete numbers are grouped into runs. Live = `dynamic` manifest and the newest chunk is only seconds old.
2. Build the EDL in memory (≈430 KB for 2 h).
3. `loadfile` with `hr-seek=yes`, `keep-open=yes`, `hwdec=d3d11va|mediacodec|no`, `osc=no`, `input-default-bindings=no`, `config=no`.
4. UI time = mpv `time-pos` − first segment's tfdt (S2 confirms). Markers: `entry.time − (sessionStartUtc − timelineStartUtc)` (S5 validates).
5. CacheFollower rebuilds the EDL, keeping the position, when Steam trims the head or playback reaches the live end.

**Export path (desktop):** `RangeSource(video)` → ffmpeg stdin, `RangeSource(audio)` → temp `.m4a` → `-c copy -avoid_negative_ts
make_zero -movflags +faststart`. That's final for Trim only / Save clip; otherwise it goes on to `Pipeline.EncodeAsync(...)`.
**Export path (phone):** local or downloaded chunks in range → Media3 Muxer (lossless, audio-sync as a timestamp shift) → final, or
Transformer re-encode → MediaStore.

---

## 4. Feature spec

### 4.1 Requirements → features
| Req | Feature | Phase |
|---|---|---|
| 1 Efficient OSS codecs | ffmpeg SVT-AV1/Opus default, NVENC/QSV/AMF, x264/x265; libmpv decode | 3, 4 |
| 2 GPU + CPU | Encoder picker with HW proof test; "Decoding: GPU (auto) / CPU" in Settings; GPU thumbnails with CPU fallback | 2–3 |
| 3 FastEncoder | Linked Core, identical preset tiles, size estimate, "Open in FastEncoder" | 3 |
| 4 Design language | Mica, 52 px title bar, `Card` style, brand gradient #7C3AED→#2563EB→#06B6D4, staggered entrance, emoji tiles, BusyLine shimmer, toasts, dashed empty states, monospace timecodes, Fluent glyphs. Android uses the same palette/shapes | 2, 4 |
| 5 By game | Games rail; per game: Cache strip, Clips grid (filters: saved/downloaded/★, map/mode chips, event counts), Recordings, Exports | 1–2 |
| 6 Steam parity, better | Timeline bands + markers, phases/tags, next/prev event, replay, clip, export. Better: broken/trimmed sessions play, live cache follow, instant seek, trickplay, frame step, 0.25–4×, A-B loop, live sync, cross-library event search, highlight reel, storage view, resume, remote libraries, no Steam needed | 2–5 |
| 7 Cuts, sync, speed | Several cuts, lossless or frame-exact; speed 0.25–4×; volume; mute; sync slider heard live, nudges, Detect, apply to game; fix timing drift | 3, 4 |
| 8 Android, secure SMB | SMB 3.1.1 encrypted, read-only, server profiles, download-first, editing + export on the phone | 4 |

### 4.2 Desktop UI
```
┌ logo SteamPoweredPlayer · your Steam recordings, without Steam ───────────────── [library size] [GPU name] ┐
│ ┌ Games ─────────┐ ┌ hero (library_hero_blur + logo) ── <Game> ─────────────────────────────────────────┐ │
│ │🔍 search       │ │ CACHE · temporary · Steam keeps the last 2 h                   ● recording now      │ │
│ │▣ Game A    25  │ │ ┃▓▓▓ session 1 ▓▓┃   gap   ┃▓▓▓▓▓▓▓ session 2 ▓▓▓▓▓▓▓▓▓▓▓▶ live┃                   │ │
│ │▣ Game B    51  │ │  ·💀··🔫·💣····🏆··📷··                                [▶ Replay] [✂ Save clip]   │ │
│ │…               │ │ CLIPS  [All][Saved][Downloaded][★]   sort ▾   Map: … ✕                          │ │
│ └────────────────┘ │ ┌thumb─────┐ ┌thumb─────┐ ┌thumb─────┐        RECORDINGS · EXPORTS below          │ │
│                    └──────────────────────────────────────────────────────────────────────────────────────┘ │
│ PLAYER (FastEncoder 3 columns 310 | * | 392): Up next │ video + markers lane + filmstrip/trim + transport   │
│                                                 + edits card (speed · volume · mute · sync + Detect)        │
│                                                 │ Events list (filters) + Export card (preset tiles)        │
│ status: Ready · indexing ▁▁▁                                  [Downloads 2]  [Stop]  [✂ Export cut]       │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 4.3 Network libraries: download-first on wireless, stream on wired
Applies to the Android app and to the desktop app when the library is on another machine (UNC path or mapped network drive).

**Link detection (no polling):**
- Android: `ConnectivityManager.registerDefaultNetworkCallback`. `TRANSPORT_ETHERNET` = wired; `TRANSPORT_WIFI` / `CELLULAR` = wireless.
  When a VPN (e.g. Tailscale) is the default, classify by the underlying transport (verify in S9).
- Windows: `GetBestInterfaceEx` to the server address → `NetworkInterfaceType` (Ethernet vs Wireless80211). VPN adapters are
  classified by the route of their underlying interface (S9). `NetworkChange.NetworkAddressChanged` re-evaluates.
- The user can override per server profile: **Auto / Always stream / Always download**.

**Wired (stream):** the loopback range server (phone) or direct UNC reads (desktop), with a read-ahead of N chunks. Seek budgets are in §5.

**Wireless (download-first):**
- **The unit is Steam's own chunk file** (3 s, ≈8 MB at 22 Mbps). Chunks are immutable once complete, so each is fetched once,
  checked by size, written as `.part` and renamed when complete. A dropped connection loses at most the chunk in flight.
- **Priority queue:** the chunks at the playhead, then forward, then the rest of the selection. A seek reprioritises. 2–3
  parallel reads per session, fewer when retries rise.
- **Resilience:** per-chunk retries with exponential backoff and jitter; reconnect on `NetworkCallback.onAvailable`; failover
  between server profiles mid-download (LAN ↔ VPN). Android runs it as a WorkManager job (foreground notification) that
  survives the app closing and resumes after a reboot. User constraints: unmetered only, charging only.
- **The player only reads local files** (an EDL over cache paths), so network loss never reaches the decoder. If the playhead
  catches up with the download edge, playback pauses on a "Downloading 63 % · 4.1 MB/s" overlay and resumes on its own. Scrubbing
  inside the downloaded part is instant.
- **Download only what's needed:** a whole recording, an A–B range, or "the ±30 s around each marker". Edits and exports
  always use original chunks, only those in range.
- **Smaller downloads (optional):** the desktop can create **viewing proxies** (e.g. 720p HEVC ≈ 3 Mbps with NVENC, about 7× smaller,
  same chunk timing) in the data folder. Remote clients prefer a proxy for watching and switch to the originals for export.
- **Local cache:** a size cap with LRU eviction, "Keep offline" pins, and a clear view of what's on the device.

### 4.4 Android
- **Targets:** `minSdk 29` (Android 10), `targetSdk 36`, arm64-v8a. Tested on an Android 13 phone. Hardware HEVC/H.264 decode
  and encode is assumed; AV1 support is detected at runtime (`MediaCodecList`). AV1 presets show only where an encoder exists,
  and dav1d is the software decode fallback.
- **Server profiles:** name, host (IP or hostname; either works), port 445, share, path, user, domain, password (Keystore),
  "require encryption" (on), network mode (Auto/Stream/Download). Profiles that reach the same library (same `library-id`)
  are grouped. The app tries the first reachable one (LAN before VPN, short timeouts), shows the active one as a chip, and you
  can switch manually.
- **Read-only:** GENERIC_READ opens with share read+write+delete. Nothing is ever written to the share. Edits, positions,
  favourites, downloads and exports stay on the phone.
- **Library:** `library.json` + art + trickplay from the desktop's data folder (a few MB, downloaded once and refreshed on change
  notify). If they're missing, the app scans the folders itself, showing ids instead of names and no art.
- **Player:** gestures (drag-seek with a trickplay bubble, double tap ±10 s), markers lane, next/prev event, speed chips, live sync,
  A-B loop, cache follow (wired) or the download overlay (wireless).
- **Editing:** the same as the desktop, plus the Kotlin port of `AudioSync` (checked against the C# version on shared fixtures).
- **Presets:** ✂️ Trim only (lossless), ⚖️ Balanced HEVC, 📱 Compact 720p HEVC, 🌐 Universal H.264, 🎯 Fit to size; AV1 where available.

### 4.5 Delete, with safety rails
- **Off by default.** Settings → "Allow deleting clips" carries an explanation and must be switched on once. It's never
  available on Android, because the phone's access is read-only.
- **Scope:** saved/downloaded clips (`clips/clip_*`) and the app's own export files. **Never** cache sessions (Steam manages
  them), timelines, `gamerecording.pb`, or anything else.
- **Path guard (unit-tested, fuzzed):** resolve the final path; refuse reparse points/junctions/symlinks; the parent must be exactly
  `<BackgroundRecordPath>\clips` and the name must match `^clip_(\d+|downloaded)_\d{8}_\d{6}$`; the folder must contain
  `clip.pb`. Exports must be files the app recorded as its own outputs. Any mismatch refuses, logs and explains.
- **Confirmation:** a dialog with thumbnail(s), titles, count, total size, and "Steam will no longer show this clip". The
  default button is **Cancel**. For ≥ 5 items or ≥ 5 GB, the user types the item count to confirm.
- **Recycle Bin by default** (`IFileOperation` with `FOFX_RECYCLEONDELETE`), with an Undo toast that restores. Permanent delete
  needs Shift + Delete and a second confirmation. On network paths, where Windows has no Recycle Bin, it's always permanent and
  always needs the typed confirmation.
- **Safety:** files in use, or a session Steam is still writing, are refused. Every delete is audited (`delete-log.jsonl` in the
  data folder: when, what, size, recycle/permanent).
- **Steam's index:** deleting a clip folder doesn't update `gamerecording.pb`. Spike S10 checks how Steam reacts. If Steam
  misbehaves, the feature only runs while Steam is closed, or points the user to Steam's own delete.

### 4.6 SMB security
Required: dialect ≥ 3.0, encryption on (AES-256-GCM > AES-128-GCM > CCM), signing, read-only opens. "Allow unencrypted" is a
per-profile override, off by default, with a warning banner. The connection screen shows dialect, cipher and signing. The
troubleshooting guide for "access denied on an encrypted share" (event 1046, mixed mode) is in `docs/smb-security.md`.

---

## 5. Roadmap

### Performance budgets (`--bench` / instrumented tests fail if missed)
| Metric | Desktop, local | Wired network client | Wireless client (download-first) |
|---|---|---|---|
| Library visible from index | < 400 ms | < 800 ms | < 2 s (index cached locally after the first time) |
| Open → first frame | < 250 ms | < 500 ms | < 3 s on a healthy link (first 2 chunks), < 250 ms once downloaded |
| Seek | < 150 ms anywhere | < 400 ms | < 150 ms (desktop) / < 300 ms (phone) inside the downloaded part |
| Stalls caused by packet loss | — | ≤ 1 per 10 min | **0** inside the downloaded part |
| Re-downloaded bytes after a drop | — | — | ≤ 1 chunk per drop |
| CPU, 1440p60 HEVC playback | < 3 % | < 5 % | < 15 % of one big core (phone) |
| RAM | < 250 MB | < 250 MB | < 200 MB (phone) |
| Lossless 30 s export | < 1 s | < 5 s | < 10 s from downloaded chunks |
| Trickplay for a 2 h session | < 40 s in the background | reads sheets | reads sheets |

### Phase 0: Spikes (`spikes/NNN-name/`, throwaway, each ends with `Verdict: VALIDATED | PARTIAL | INVALIDATED`)
| # | Spike | Given / When / Then | Risk |
|---|---|---|---|
| S2 | `edl-mp4dash-cli` | Given portable mpv ≥ 0.42 and an EDL over a 2 h session with a stale manifest, when played, then start < 250 ms, seek to 1:00:00 < 150 ms, A/V in sync, `audio-delay` live, speed 0.25/2/4, frame-step, gaps handled, time-pos origin recorded. Cache, **on copies only**: a chunk mpv has open can still be deleted; deleting head chunks under playback is survivable; EOF → re-EDL continues | High |
| S1 | `winui-mpv-d3d11` | Given WinUI 3 + the libmpv D3D11 render API in a `SwapChainPanel`, when playing the S2 EDL, then no tearing, CPU < 3 %, XAML draws over the video, resize/fullscreen work. Fallback `wid` | High |
| S3 | `android-smb-download` | Given an Android 13 phone + JCIFS 3 (read-only, encryption required) and two profiles (LAN, VPN), when downloading a 5 min range over Wi-Fi with injected drops (Wi-Fi toggled, AP roaming), then 0 corrupt chunks, ≤ 1 chunk re-fetched per drop, automatic resume and profile failover, and playback from local chunks never stalls inside the downloaded part. The server shows an encrypted 3.1.1 session | High |
| S3b | `smb-stream-wired` | Given a wired client, when streaming the EDL over SMB (loopback range server on Android, direct reads on Windows), then the wired budgets hold. A throwaway Python `smbprotocol` client on any wired machine serves as the reference | Medium |
| S9 | `link-detection` | Given Wi-Fi, Ethernet (USB adapter) and VPN-over-each, when the default network changes, then Android and Windows classify each correctly and switch modes without polling | Medium |
| S7 | `android-export` | Given a 30 s downloaded range, when the Media3 Muxer builds the lossless file with a +300 ms audio shift and Transformer re-encodes at 2×, then both are in sync and full-range colour survives | Medium |
| S5 | `timeline-alignment` | Given 3 sessions with deaths/kills, when markers are mapped with the UTC rule, then they land within ±1 s of the visible event | Medium |
| S8 | `android-detect` | Given FastEncoder's synthetic "sound 300 ms late" clip, when the Kotlin `AudioSync` port runs on the phone, then it reports 300 ± 10 ms, matches C#, and analyses 75 s in < 30 s | Medium |
| S10 | `steam-clip-delete` | **Needs maintainer approval (writes in Steam's folder).** Given a throwaway clip, when its folder is moved to the Recycle Bin with Steam running and then with Steam closed, then record what Steam shows and whether it repairs `gamerecording.pb` | Medium |
| S4 | `trickplay-cost` | Given init + the first 512 KB of each chunk, when decoded keyframe-only on the GPU in one long-lived ffmpeg, then ≤ 15 ms and ≤ 0.6 MB per chunk | Low |
| S6 | `fastencoder-link` | Given a console harness linking FastEncoder Core, when `Pipeline.EncodeAsync` runs GPU Turbo on a range file with `AudioDelay=0.3`, then it builds with no FastEncoder change and comes out in sync | Low |

Setup: `tools/bootstrap.ps1` fetches pinned mpv/libmpv, JDK 21, Android cmdline-tools/platform-tools/SDK/build-tools and Gradle
into `.tools/`. Phone: Developer options → Wireless debugging → `adb pair`, with adb keys under `.tools/`.

### Phase 1: Core library (TDD, no UI)
Files: `src/SteamPoweredPlayer.csproj`; `src/Core/Steam/{SteamConfig,FolderNames,GameIds,SteamPaths,Vdf,RecordingPb,Timeline}.cs`;
`src/Core/Steam/Proto/steamrecording.proto`; `src/Core/Library/{Session,Scanner,Index,IndexStore,Artwork,DataFolder}.cs`;
`src/Core/Playback/Edl.cs`; `src/Core/Export/{RangeSource,DeletePolicy}.cs`; `src/Core/Net/{ChunkPlan,LocalChunkCache}.cs`;
`tests/SteamPoweredPlayer.Tests/*`; `tests/fixtures/` (synthetic DASH sessions, tiny `.pb`/timeline/VDF samples);
`tests/fixtures/golden/*.json`.

csproj (FastEncoder linked; packages restored into `.tools/nuget`):
```xml
<PropertyGroup>
  <FastEncoderDir Condition="'$(FastEncoderDir)' == ''">$(MSBuildThisFileDirectory)..\external\FastEncoder\</FastEncoderDir>
  <RestorePackagesPath>$(MSBuildThisFileDirectory)..\.tools\nuget</RestorePackagesPath>
</PropertyGroup>
<ItemGroup Label="FastEncoder (linked, read-only)">
  <Compile Include="$(FastEncoderDir)src\Core\Ff.cs"        Link="FastEncoder\Ff.cs" />
  <Compile Include="$(FastEncoderDir)src\Core\Encoders.cs"  Link="FastEncoder\Encoders.cs" />
  <Compile Include="$(FastEncoderDir)src\Core\Presets.cs"   Link="FastEncoder\Presets.cs" />
  <Compile Include="$(FastEncoderDir)src\Core\Pipeline.cs"  Link="FastEncoder\Pipeline.cs" />
  <Compile Include="$(FastEncoderDir)src\Core\AudioSync.cs" Link="FastEncoder\AudioSync.cs" />
</ItemGroup>
```

| Task | Content | Key tests |
|---|---|---|
| 1.1 FolderNames | Regexes from the format doc, UTC parsing | `bg_730_20250101_120000` → CacheSession, 730, 2025-01-01T12:00:00Z; `timeline_73020250101_120000.json` → gameid 730 (no separator) |
| 1.2 SteamConfig | Steam path (registry), user (`loginusers.vdf` MostRecent), **only** the `GameRecording` block | `120min` → 2 h; other forms (`24h`?) verified against what Steam writes |
| 1.3 GameIds | `IsShortcut`, `ShortcutAppId`, `FromShortcut`; names: shortcuts.vdf → appmanifest (all libraries) → appinfo.vdf → index → "App N"; art copied once | `FromShortcut(2400000000) == 10307921510433554432` |
| 1.4 RecordingPb | Reverse-engineered `.proto`; unknown fields kept | Synthetic samples round-trip; opt-in real-library run parses every `clip.pb` with 0 errors (read-only) |
| 1.5 Scanner | `SegmentRun`, `SessionMedia(Folder, Runs, SegmentSeconds, Live, Temporary)` | normal, trimmed head, stale manifest, gap → 2 runs, live, crashed (zero MPD + old `.tmp`), missing audio chunk, folder deleted mid-scan; < 20 ms for 4 800 entries |
| 1.6 Edl | Builder (sketch below) | Golden EDL; 2 400 segments < 5 ms; escaping of paths with commas/unicode |
| 1.7 RangeSource | Seekable stream over `[init, chunk_a … chunk_b]`, `FileShare.ReadWrite \| Delete` | Bytes equal `cat`; deleting a chunk of a fixture copy while open succeeds |
| 1.8 DataFolder + Index | First-run proposal + confirmation, `library-id`, `library.json` v1 (atomic replace), never inside `BackgroundRecordPath` | Round-trip; 200-recording index < 1 MB, parse < 50 ms |
| 1.9 DeletePolicy | Pure path guard of §4.5 | `..`, junctions, UNC, 8.3 names, case variants, non-clip folders, cache sessions → all refused |
| 1.10 ChunkPlan + LocalChunkCache | Priority order from the playhead/selection, `.part` → rename, size check, LRU cap, pins | Interrupted writes never appear complete; eviction never removes pinned or playing chunks |

```csharp
public static string Build(SessionMedia s, Func<string, string> url)   // url: local path, cache path, or loopback URL
{
    var sb = new StringBuilder("# mpv EDL v0\n!no_chapters\n");
    Track(sb, s, 0, url); sb.Append("!new_stream\n"); Track(sb, s, 1, url);
    return sb.ToString();
}
static void Track(StringBuilder sb, SessionMedia s, int st, Func<string, string> url)
{
    sb.Append("!mp4_dash,init=").Append(Esc(url(Path.Combine(s.Folder, $"init-stream{st}.m4s")))).Append('\n');
    foreach (var r in s.Runs)
        for (int n = r.First; n < r.First + r.Count; n++)
            sb.Append(Esc(url(Path.Combine(s.Folder, $"chunk-stream{st}-{n:00000}.m4s"))))
              .Append(",length=").Append(s.SegmentSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
}
static string Esc(string v) => $"%{Encoding.UTF8.GetByteCount(v)}%{v}";
```

### Phase 2: Desktop player MVP (detailed after the S1/S2 verdicts)
Shell (Mica, title bar, BusyLine, toasts, FastEncoder styles) → `Mpv.cs` (wakeup-driven event loop) → `MpvD3D11Renderer` → Library
page (games rail, Cache strip, grids, filters, search) → Player page (markers lane with icon map, trickplay filmstrip, transport,
keys, resume, A-B loop, fullscreen overlay) → CacheFollower → trickplay generator → FSW index → first-run card → `--selftest`,
`--bench`.

### Phase 3: Desktop edits, export, delete, network libraries
Cuts, speed/volume/mute, live sync + nudges, **Detect** (FastEncoder `AudioSync`), apply to game, fix timing drift, preset grid +
size estimate, export queue with taskbar progress, **Save clip** (markers as chapters), "Open in FastEncoder", highlight reel,
screenshots, colour-range check per encoder, **export folder picker on first export**, **DeleteService** (§4.5, after S10),
**network libraries** (LinkDetector, ChunkDownloader, Downloads view, optional viewing proxies).

### Phase 4: Android
1. Toolchain + Compose skeleton + brand theme. 2. Server profiles, Keystore, endpoint chooser, encryption switch, connection
info. 3. LinkDetector + ChunkDownloader (WorkManager) + LocalChunkCache. 4. IndexRepository (change notify) + fallback scan,
golden tests. 5. Player over local chunks; loopback streaming when wired. 6. Editor + AudioSync port. 7. Export (RangeMuxer +
Transformer, presets, MediaStore, progress notification). 8. Downloads screen (queue, pins, cache size).

### Phase 5: Beyond Steam (each opt-in)
Storage view + batch "compress old clips", cross-library event search, smart cut (re-encode only the GOPs at the cut edges),
achievement names/icons, a highlight reel on the phone. (Linux/macOS clients are out of scope.)

---

## 6. Testing
- xUnit for all of Core. Real-library tests are opt-in (`SPP_REAL_LIBRARY`) and strictly read-only.
- Golden fixtures shared by the C# and Kotlin parsers and EDL builders.
- `--selftest`: synthetic sessions for every case above, headless libmpv seek accuracy ±1 frame, every preset, sync (300 ms
  injected must be detected and fixed), colour range, a Steam-style head trim during playback, **an interrupted download**
  (connection killed mid-chunk) that must resume without corruption, and the **delete path guard**.
- `--bench` against §5, results in `docs/bench.md` (hardware class only, no machine names).
- Android: JUnit (parsers, EDL, ChunkPlan, endpoint chooser, Keystore wrapper); instrumented tests on a real phone for playback,
  download-under-loss, export and Detect.
- UI: screenshots of each view; an exploratory QA pass per phase.

## 7. Agent skills used (Hermes)
`plan`, `spike`, `test-driven-development`, `systematic-debugging`, `video-processing`, `github-repo-management`,
`requesting-code-review`, `simplify-code`, `computer-use`, `dogfood`, `sketch` (optional UI mockups before Phase 2).

## 8. Risks
| Risk | Impact | Mitigation |
|---|---|---|
| libmpv D3D11 render API is new | Embedding bugs | S1 first; `wid` fallback |
| EDL `mp4_dash` "may change" | A future mpv breaks playback | Pin libmpv; `stream_cb` fallback; selftest on upgrade |
| Holding a cache chunk open blocks Steam's cleanup | The cache can't trim | Share-delete opens; S2 checks mpv's own opens; feed bytes ourselves if needed |
| Steam trims while you watch/export/download | Errors mid-operation | CacheFollower; downloads/exports report "Steam already removed X s" |
| Wireless packet loss | Stutter, corrupt files | Download-first, per-chunk `.part` + size check, the player reads local files only |
| Steam format/config changes | Missing recordings | Golden tests, "unknown layout" diagnostics, never write Steam's folder |
| Delete mistakes | Data loss | Off by default, path guard, Recycle Bin, typed confirmation, audit log, S10 |
| FastEncoder submodule drifts | Build breaks after a FastEncoder change | Submodule pinned to a commit; bump deliberately with `--selftest` green on both projects |
| FastEncoder's shared temp folder cleanup (`CleanupTemp()`) | Rare loss of two-pass logs | SPP never calls it; a proper fix needs a FastEncoder change (maintainer's call) |
| JCIFS on Android | Build/runtime issues | S3; smbj fallback |
| Licence obligations when binaries ship | GPL requires source + licence texts for bundled libmpv/FFmpeg | GPL-3.0-or-later chosen (D19); release packaging adds `docs/third-party.md`, licence texts and the exact libmpv/FFmpeg versions/sources |
| Personal data leaking into the repo | Privacy | Placeholders and synthetic fixtures only; maintainers keep local pre-commit checks |

## 9. Open questions
None blocking. Next step: maintainer approval to start Phase 0 (spikes).

Resolved in rev 3.1: FastEncoder is published and used as a submodule (D5); Linux is out of scope; licence GPL-3.0-or-later (D19).
