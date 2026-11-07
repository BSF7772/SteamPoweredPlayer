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
| 5 | **Phase 1 approved (2026-11-07).** S3b and S9 deferred ("later", after Phase 4); S10 and every delete/clean-up feature moved to the **last phase** (Phase 6). FastEncoder submodule tracks its latest `main` (no separate pin confirmation). `tools/bootstrap.ps1` merged with task 1.11 and moved to Phase 2. Network resilience rule: reconnect on its own after drops and Wi-Fi → Wi-Fi changes, give up after **30 s** without reaching the server (§4.3). 2× phone export: accurate colours by default, verified against the source, a hidden toggle for the fast path (§4.4). Core is its own project (`src/Core/SteamPoweredPlayer.Core.csproj`) so tests never reference the WinUI app |
| 4 | **End of Phase 0 (S2, S4, S5, S6 measured; S1, S3, S3b, S7, S8, S9, S10 still planned).** Playback uses the **lead EDL** (first part `N0·3 s`, the part before each gap stretched) so mpv `time-pos` equals the tfdt media time, **every seek that crosses chunks is a reload** (`loadfile <edl> start=<t>`, because revisiting an opened chunk breaks decode), mpv is **pre-warmed** at app start, and a **~60-chunk window EDL** with a reload policy bounds RAM and handles; the scanner **checks chunk heads** (zero-filled chunks exist); mpv's share mode is READ\|WRITE\|DELETE. Trickplay reads the chunk's **first fragment** (256 KiB first read plus an exact second read), not a fixed 512 KB prefix; default sheet 160 px JPEG q10 every 3 s (about 4 MB/h). Markers use **Steam's exact millisecond offset** (`gamerecording.pb` / `clip.pb` field 1.5.10), the folder-name time (±1 s) is a fallback; marker position equals media tfdt. FastEncoder is the submodule `external/FastEncoder` (relative URL `../FastEncoder.git`, pin ≥ `b4ccf39`, not added yet); default export preset TBD; AudioSync is Low confidence on real gameplay, so export has a "not applied" path. New Phase 1 task 1.11 (ffmpeg + libmpv shipped beside the exe from a pinned manifest, `docs/third-party.md`). The fixed "< 3 % CPU" budget is replaced by a relative, reproducible CPU-efficiency definition. SMB client requirement added to §4.6. Phase 0 rows carry a status |

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
| D3 | Opening recordings | Own chunk index → **lead EDL** over a ~60-chunk window (`!mp4_dash,init=` + `!new_stream`), seek-by-reload (S2) | MPD via ffmpeg/mpv | MPD fails on real sessions. Fallback: libmpv `stream_cb` virtual fMP4 |
| D4 | Export | Stage 1 lossless range (byte-concat → `ffmpeg -c copy`); stage 2 FastEncoder `Pipeline` on that file | Re-encode from the MPD | Fast, robust, reuses every preset/sync/CFR fix |
| D5 | FastEncoder | **Git submodule `external/FastEncoder`** (relative URL `../FastEncoder.git`, pinned commit ≥ `b4ccf39`, not added yet); its Core sources are linked into the build (`$(FastEncoderDir)`, defaulting to the submodule; overridable to a side-by-side checkout for joint development); "Open in FastEncoder" hand-off | Fork / copy / NuGet package | One source of truth, reproducible builds for anyone cloning with `--recursive` |
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
0. **Pre-warm:** at app start, open mpv once on a tiny 2-chunk EDL. S2 measured open → first frame at 302 ms in a fresh process
   (misses the 250 ms budget) and 95 ms after pre-warm (80 ms for a repeat open).
1. One directory listing per session. Segment N is complete when both `chunk-stream0-N` and `chunk-stream1-N` exist without
   `.tmp` **and their heads are not zero-filled** (an 8-byte head read; S2 and S4 both found zero-filled chunks with normal names,
   for example the last chunks of a stale session or the only chunk of a crashed one). Complete numbers are grouped into runs. Live =
   `dynamic` manifest and the newest chunk is only seconds old.
2. Build the **lead EDL** in memory: first part `length = N0·3 s` (N0 = first chunk number), and the part before each gap is
   stretched to `3·(next − n)` s, so the EDL axis equals the tfdt axis and mpv `time-pos` == the session's media time. The classic
   every-part-`length=3` EDL fails (S2: seek error median −986 ms, audio missing after 40 of 40 seeks, a gap freezes video for 29 s).
   Use a **window EDL** of about 60 chunks around the position, not the whole session: RAM grows about 0.23 MB per media-second
   (206 MB at open, 431 MB after 16 media-minutes) and open handles grow about 1.7 per chunk (mpv never closes played chunks).
   The cost of the window is the reload (95 ms median), not building the string (budget in task 1.6: 2 400 segments < 5 ms).
3. `loadfile` with `hr-seek=yes`, `keep-open=yes`, `hwdec=d3d11va|mediacodec|no`, `osc=no`, `input-default-bindings=no`, `config=no`.
   **Every user seek or back-step that crosses chunks is `loadfile <edl> start=<t>` (seek-by-reload).** S2: `seek absolute+exact`
   to an already-opened chunk fails 12/12 revisits (HEVC POC errors, audio +1.6 s, time-pos +9…+15 s) because mpv skips
   `demux_seek` for dash on a re-entered segment; reload had 0/30 failures at 95 ms median (60-chunk window, hardware decode;
   CPU decode 247 ms). Frame-step forward is fine; frame-step back across a chunk boundary is done by reload. Reload periodically
   during long sequential play to bound RAM and handles (431 → 326 MB on one reload). mpv opens chunks with READ|WRITE|DELETE share
   (0 denials over 100 files); whether Steam's delete succeeds while mpv holds a chunk is **not yet tested**.
4. UI time = `time-pos` (already media time with the lead EDL; subtract the first tfdt only for a 0-based display; the origin is
   about first tfdt − 33 ms, not 0). Compute duration from the index (chunks × 3 s), because mpv `duration` is unreliable with an
   EDL. **Markers:** position = `entry.time/1000 − offset/1000`, where `offset` is Steam's exact millisecond value from
   `gamerecording.pb` (the timeline entry's field 1.5.10) or `clip.pb` (field 1.5.10); that is an absolute tfdt, it survives
   trimming, and clips restart chunk numbers but not tfdt. Fallback only when the field is missing: the folder-name rule
   `entry.time − (sessionStartUtc − timelineStartUtc)`, good to about ±1 s (S5: 4 of 18 sessions beyond ±0.5 s, one clip marker at
   0.89 s). Multi-kill markers (`cs2_double_kill`, `cs2_multi_kill`, duration about 8 s) are ranges; "jump to marker" lands 2–3 s early.
5. CacheFollower rebuilds the window EDL, keeping the position, when Steam trims the head or playback reaches the live end (S2: EOF →
   rebuilt EDL → `loadfile start=` continues within 0.03 s, first frame 169 ms).

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
  When a VPN (e.g. Tailscale) is the default, classify by the underlying transport (verify in S9). S3 measured that on the test
  phone the VPN's capabilities carry the underlying transport (`wifi+vpn`, `cell+vpn`), but the VPN *network* never reports Wi-Fi
  coming or going. So also register a callback on a non-VPN `NetworkRequest`; it reports the real networks (S3, verified).
- Windows: `GetBestInterfaceEx` to the server address → `NetworkInterfaceType` (Ethernet vs Wireless80211). VPN adapters are
  classified by the route of their underlying interface (S9). `NetworkChange.NetworkAddressChanged` re-evaluates.
- The user can override per server profile: **Auto / Always stream / Always download**.

**Mobile data, metered networks, Data Saver (maintainer 2026-11-07: a toast, nothing else):**
- When a transfer starts on a metered network, or Data Saver is on, show **one toast-style notice with "Don't show again"**. Then the
  app stops caring: no cap, no prompt, no pause button, no Wi-Fi-only mode, no time budget for slow links. Android's own Data Saver
  and data limits stay the user's tools.
- The app shows only **the data downloaded so far from the source** (MB, per download and in total), next to the progress it already shows.
- Detection: the default network lacks `NET_CAPABILITY_NOT_METERED` (API 21; Android advises this over checking the transport, so a
  metered Wi-Fi counts and a free cell plan doesn't), or `getRestrictBackgroundStatus()` (API 24) reports Data Saver on. Re-checked
  on `onCapabilitiesChanged` and on `ACTION_RESTRICT_BACKGROUND_CHANGED` (runtime receiver only).
- Through a VPN this works as is. Measured on the test phone: Tailscale's VPN takes its metered flag from the network underneath
  (Wi-Fi: `WIFI|VPN`, not metered; mobile data: `CELLULAR|VPN`, metered). Other VPN apps targeting Android 10+ are metered by default
  unless they call `VpnService.Builder.setMetered(false)`. Worst case is a toast that isn't needed, once.
- WorkManager job: `NetworkType.CONNECTED`.
- Test without spending mobile data: `adb shell cmd netpolicy set metered-network <ssid> true`.
- The spike's fallback byte cap (S3) was test scaffolding and is not carried into the product.

**Wired (stream):** the loopback range server (phone) or direct UNC reads (desktop), with a read-ahead of N chunks. Seek budgets are in §5.

**Wireless (download-first):**
- **The unit is Steam's own chunk file** (3 s, ≈8 MB at 22 Mbps). Chunks are immutable once complete, so each is fetched once,
  checked by size, written as `.part` and renamed when complete. A dropped connection loses at most the chunk in flight.
- **Priority queue:** the chunks at the playhead, then forward, then the rest of the selection. A seek reprioritises. 2–3
  parallel reads per session, fewer when retries rise.
- **Disconnects and network changes (maintainer 2026-11-07):** a dropped connection, a reconnect, and a change from one Wi-Fi to
  another (roaming or a different SSID) are handled on their own: the transfer pauses, reconnects when a network is back, and
  resumes from the chunk in flight. If the server can't be reached for **30 s** (for example after switching to mobile data, where
  the LAN address is unreachable), the attempt times out: the next server profile is tried if there is one (LAN → VPN), otherwise
  the download shows "Can't reach <profile>" with Retry and resumes when a network change event arrives.
- **Resilience:** per-chunk retries with exponential backoff and jitter; reconnect on `NetworkCallback.onAvailable`; failover
  between server profiles mid-download (LAN ↔ VPN). Android runs it as a WorkManager job (foreground notification) that
  survives the app closing and resumes after a reboot. Mobile data: see above (a toast only).
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
- **2× re-encode colour (maintainer 2026-11-07):** the default is the **accurate** path: a software decoder for full-range input
  (S7: the hardware decoder clips it), even though it costs time and power (1.83× instead of 2.8× realtime). The BT.601 tag the
  hardware encoder writes is accepted only after an instrumented test compares the output with the original (SSIM/PSNR and mean
  colour per channel against the source, same frames) and shows it is accurate. A **hidden toggle** (developer options, e.g. tap
  the version 7×) switches to the fast hardware-decode path for users who prefer speed over exact colours.
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
Client requirement: the app's SMB client must negotiate SMB 3.x encryption when the server or share requests it (and signing when
required). A client that cannot is reported clearly (dialect, cipher and signing are shown on the connection screen) and is never
silently downgraded.

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
| CPU efficiency, 1440p60 HEVC playback | as efficient as possible; relative, see below | same | same (phone) |
| RAM | < 250 MB | < 250 MB | < 200 MB (phone) |
| Lossless 30 s export | < 1 s | < 5 s | < 10 s from downloaded chunks |
| Trickplay for a 2 h session | < 40 s in the background (S4: about 5.6 s wall, hardware decode) | reads sheets | reads sheets |

**CPU efficiency is relative and reproducible.** Every CPU figure states the hardware class (CPU model class, cores/threads, ISA, GPU,
decoder in use) and the method: process CPU time per media-second, reported as % of one core and % of all logical cores. It is
compared with a reference run on the SAME machine: mpv CLI with the same hwdec and the same file. Target: the app's overhead over
that reference as small as practical; regressions are tracked in `--bench`. (S2 reference on a desktop NVIDIA GPU, 16 logical cores,
mpv CLI, `d3d11va`: 5.2 % of one core = 0.32 % of all logical cores at 1×; CPU decode 77.7 % of one core.)

Notes from S2 on the other budgets: open → first frame needs the pre-warm (302 ms fresh, 95 ms pre-warmed); seek < 150 ms holds
only as seek-by-reload with hardware decode (95 ms median, 9/90 reloads over 150 ms; CPU decode 83/90 over); RAM < 250 MB holds only
with the window EDL and periodic reload (431 MB after 16 media-minutes of whole-session EDL).

### Phase 0: Spikes (`spikes/NNN-name/`, throwaway, each ends with `Verdict: VALIDATED | PARTIAL | INVALIDATED`)
| # | Spike | Given / When / Then | Risk | Status / result |
|---|---|---|---|---|
| S2 | `edl-mp4dash-cli` | Given portable mpv ≥ 0.42 and an EDL over a 2 h session with a stale manifest, when played, then start < 250 ms, seek to 1:00:00 < 150 ms, A/V in sync, `audio-delay` live, speed 0.25/2/4, frame-step, gaps handled, time-pos origin recorded. Cache, **on copies only**: a chunk mpv has open can still be deleted; deleting head chunks under playback is survivable; EOF → re-EDL continues | High | **PARTIAL.** The EDL as first sketched fails (seeks land up to 3 s early, audio is lost after a seek, gaps freeze for 30 s). The **lead EDL** plus seek-by-reload (`loadfile <edl> start=<t>`), a pre-warmed mpv and a ~60-chunk window meets the budgets with hardware decode (open 95 ms pre-warmed, seek 95 ms median, 0/30 reload failures vs 12/12 failed revisit seeks). Delete-while-open and head-trim, run later on copies (s2d, indicative timings): a held chunk deletes immediately (not delete-pending) and playback survives; 20 head chunks trimmed every 3 s under playback gave 0 ms stall, 6 ms axis drift and an 88 ms reload |
| S1 | `winui-mpv-d3d11` | Given WinUI 3 + the libmpv D3D11 render API in a `SwapChainPanel`, when playing the S2 EDL, then no tearing, low CPU (relative definition, see the budgets), XAML draws over the video, resize/fullscreen work. Fallback `wid` | High | **VALIDATED (maintainer accepted RAM/CPU).** Correctness 26/26 (D3D11 render API, no `wid` needed). Perf on an idle machine (5-minute soak): CPU 1.96 % of all cores (< 3 PASS) but ≈ 5.6× the mpv CLI reference; 59.9 presents/s, 0 black frames; seek-by-reload median 96.5 ms (5 of 30 seeks > 150 ms); RAM 295 MB (over the 250 budget, accepted), growing ≈ 19 MB/min in the soak = mpv's bounded demuxer cache (≤ 200 MiB, released on reload: −67 MB). Maintainer: RAM/CPU overhead acceptable for now, only a real leak must be fixed; Phase 2 sets an explicit cache ceiling. The soak's "stall" was the harness: `keep-open` pauses at EOF and `pause` survives `loadfile`, so every reload that should play must set `pause=no` |
| S3 | `android-smb-download` | Given an Android 13 phone + JCIFS 3 (read-only, encryption required) and two profiles (LAN, VPN), when downloading a 5 min range over Wi-Fi with injected drops (Wi-Fi toggled, AP roaming), then 0 corrupt chunks, ≤ 1 chunk re-fetched per drop, automatic resume and profile failover, and playback from local chunks never stalls inside the downloaded part. The server shows an encrypted 3.1.1 session | High | **Device PASS; AP roaming and playback during download not covered.** SMB 3.1.1 AES-128-GCM + signing on LAN and VPN; 5 min = 514 MB in 49 s (10.4 MB/s); Wi-Fi drops up to 90 s: 0 failed, 0 re-fetched, 0 SHA mismatches; failover to VPN over cellular in 9 s; 20 MB cap held (20.14 MB); fail-back 37 ms after Wi-Fi validated. Device runs found and fixed 3 failover bugs, including network callbacks silent under a VPN, because the default network is the VPN: the product must also watch non-VPN networks. See spike README |
| S3b | `smb-stream-wired` | Given a wired client, when streaming the EDL over SMB (loopback range server on Android, direct reads on Windows), then the wired budgets hold. A throwaway Python `smbprotocol` client on any wired machine serves as the reference | Medium | **Later** (maintainer 2026-11-07: deferred until after Phase 4; until then every remote client is download-first) |
| S9 | `link-detection` | Given Wi-Fi, Ethernet (USB adapter) and VPN-over-each, when the default network changes, then Android and Windows classify each correctly and switch modes without polling | Medium | **Later** (deferred with S3b) |
| S7 | `android-export` | Given a 30 s downloaded range, when the Media3 Muxer builds the lossless file with a +300 ms audio shift and Transformer re-encodes at 2×, then both are in sync and full-range colour survives | Medium | **Lossless PASS, re-encode PARTIAL.** Remux bit-exact (ES MD5 = source), +300.0 ms measured, full range kept, 30 s in 1.6 s. 2× re-encode: the Qualcomm HW decoder clips full-range input, so a software decoder is needed (colours now match the source, PSNR 38–39 dB; 1.83× realtime instead of 2.8×). The HW encoder always tags BT.601, which matches its pixels. Sync after 2× within 3.4 ms. The product must detect the decoder range bug at runtime. No AV1 encoder on the test phone |
| S5 | `timeline-alignment` | Given sessions with deaths/kills (only two cache sessions with events were available; repeat as the library grows), when markers are mapped with Steam's exact millisecond offset (field 1.5.10 of `gamerecording.pb` / `clip.pb`), then they land on the visible event. The folder-name UTC rule is the fallback, accurate to ±1 s | Medium | **VALIDATED.** The exact offset from Steam's index aligns markers to the media `tfdt`; the folder-name rule is within ±1 s |
| S8 | `android-detect` | Given FastEncoder's synthetic "sound 300 ms late" clip, when the Kotlin `AudioSync` port runs on the phone, then it reports what the C# detector reports (+277 ms on this clip; maintainer decision, the C# result is the reference), with High confidence, and analyses 75 s in < 30 s | Medium | **VALIDATED.** Phone: raw inputs +277.413 ms (= C#), MediaCodec path +276.61 ms, High 12/12, ≤ 11 s per 75 s |
| S10 | `steam-clip-delete` | **Needs maintainer approval (writes in Steam's folder).** Given a throwaway clip, when its folder is moved to the Recycle Bin with Steam running and then with Steam closed, then record what Steam shows and whether it repairs `gamerecording.pb` | Medium | **Last step of the project** (Phase 6, with every delete/clean-up feature) |
| S4 | `trickplay-cost` | Given init + the first fragment (moof + IDR) of each chunk (256 KiB first read, plus an exact second read when the IDR is larger), when decoded keyframe-only on the GPU in one long-lived ffmpeg, then ≤ 15 ms and ≤ 0.6 MB per chunk (mean) | Low | **PARTIAL.** A 2 h session takes 5.3–5.6 s on the GPU; the adaptive read averages 0.27 MB per chunk (0.88 MB max). A fixed 512 KiB prefix truncates 14.9 % of IDRs and a fixed 1 MiB prefix reads 1.0 MB, so no fixed prefix is used. Sprite sheets are 3.9 MB/h at 160 px |
| S6 | `fastencoder-link` | Given a console harness linking FastEncoder Core, when `Pipeline.EncodeAsync` runs GPU Turbo on a range file with `AudioDelay=0.3`, then it builds with no FastEncoder change and comes out in sync | Low | **VALIDATED.** The delay is applied exactly (waveform correlation, ±0 ms at 1 ms resolution). `AudioSync` gives `Low` confidence on real gameplay, so the app needs a "not applied" path. A lossless 30 s range takes 390 ms warm |

Setup: `tools/bootstrap.ps1` (not written yet) will fetch pinned mpv/libmpv, JDK 21, Android cmdline-tools/SDK/build-tools and
Gradle into `.tools/` (maintainer 2026-11-07: merged with task 1.11 into one script + one pinned manifest, written when Phase 2
first needs libmpv; Phase 1 needs only the .NET SDK, and ffmpeg is not needed by the unit tests). Android platform-tools (adb) are already in `.tools/platform-tools/`. Phone: Developer options → USB
debugging (works over cable) or Wireless debugging → `adb pair`; the RSA key must be accepted once (`adb devices` shows `device`).

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
FastEncoder is the git submodule `external/FastEncoder` (relative URL `../FastEncoder.git`, pinned to a commit ≥ `b4ccf39`). The
submodule is added in Phase 1 at FastEncoder's latest `main` (maintainer 2026-11-07: there is only one version; bump it to the
latest whenever FastEncoder changes). Core lives in `src/Core/SteamPoweredPlayer.Core.csproj` (the links above go there); the
tests reference Core only. Call order matters:
`Ff.Locate()` → `await Encoders.DetectAsync()` → `Presets.Get(...)` (S6: `Build()` before detection silently returns the CPU settings).

| Task | Content | Key tests |
|---|---|---|
| 1.1 FolderNames | Regexes from the format doc, UTC parsing | `bg_730_20250101_120000` → CacheSession, 730, 2025-01-01T12:00:00Z; `timeline_73020250101_120000.json` → gameid 730 (no separator) |
| 1.2 SteamConfig | Steam path (registry), user (`loginusers.vdf` MostRecent), **only** the `GameRecording` block | `120min` → 2 h; other forms (`24h`?) verified against what Steam writes |
| 1.3 GameIds | `IsShortcut`, `ShortcutAppId`, `FromShortcut`; names: shortcuts.vdf → appmanifest (all libraries) → appinfo.vdf → index → "App N"; art copied once | `FromShortcut(2400000000) == 10307921510433554432` |
| 1.4 RecordingPb | Reverse-engineered `.proto`; unknown fields kept | Synthetic samples round-trip; opt-in real-library run parses every `clip.pb` with 0 errors (read-only) |
| 1.5 Scanner | `SegmentRun`, `SessionMedia(Folder, Runs, SegmentSeconds, Live, Temporary)` | normal, trimmed head, stale manifest, gap → 2 runs, live, crashed (zero MPD + old `.tmp`), missing audio chunk, folder deleted mid-scan, **zero-filled chunk with a normal name (an 8-byte head check rejects it)**; < 20 ms for 4 800 entries |
| 1.6 Edl | Lead EDL builder over a window of about 60 complete chunks (sketch below): first part N0·3 s, the part before each gap stretched, so mpv `time-pos` equals the `tfdt` media time. Seeking is `loadfile <edl> start=<t>` (seek-by-reload), never `seek`. Window and reload policy (RAM grows with media length) | Golden EDL (first part, gap, window edges); 2 400 segments < 5 ms; escaping of paths with commas/unicode |
| 1.7 RangeSource | Seekable stream over `[init, chunk_a … chunk_b]`, `FileShare.ReadWrite \| Delete` | Bytes equal `cat`; opening with read + write + delete sharing succeeds next to a second reader. "Deleting a chunk of a fixture copy while open succeeds" is **not proven yet** (S2 did not run it): test it here on fixture copies |
| 1.8 DataFolder + Index | First-run proposal + confirmation, `library-id`, `library.json` v1 (atomic replace), never inside `BackgroundRecordPath` | Round-trip; 200-recording index < 1 MB, parse < 50 ms |
| ~~1.9 DeletePolicy~~ | Moved to Phase 6 (delete and clean-up come last) | — |
| 1.10 ChunkPlan + LocalChunkCache + ReconnectPolicy | Priority order from the playhead/selection, `.part` → rename, size check, LRU cap, pins. `ReconnectPolicy`: backoff + jitter, pause on disconnect, resume on a network event, **time out after 30 s** without reaching the server → next profile or "Can't reach" (§4.3) | Interrupted writes never appear complete; eviction never removes pinned or playing chunks; a 29 s outage resumes, 30 s times out, a network change restarts the clock |
| 1.11 Dependencies (Phase 2, merged with `tools/bootstrap.ps1`) | The release ships ffmpeg + libmpv beside the exe. The repo holds a pinned manifest (URL + SHA-256 per binary) and a build step that fetches and verifies them. Binaries are never committed. The GPL source offer is in `docs/third-party.md` | A wrong hash fails the build; a clean checkout builds with the manifest only; `licenses/` is in the release folder |

```csharp
// Lead EDL over a window of complete chunks (S2). `chunks` = the sorted, head-validated chunk numbers of the window (about 60).
// The first part's length is N0·3 s and the part before each gap is stretched to 3·(next − n) s, so the EDL axis equals the
// tfdt axis (chunk N starts at (N−1)·3 s) and mpv time-pos == media time. Seeking is `loadfile <edl> start=<t>`, never `seek`.
public static string Build(SessionMedia s, IReadOnlyList<int> chunks, Func<string, string> url)   // url: local path, cache path, or loopback URL
{
    var sb = new StringBuilder("# mpv EDL v0\n!no_chapters\n");
    Track(sb, s, chunks, 0, url); sb.Append("!new_stream\n"); Track(sb, s, chunks, 1, url);
    return sb.ToString();
}
static void Track(StringBuilder sb, SessionMedia s, IReadOnlyList<int> chunks, int st, Func<string, string> url)
{
    sb.Append("!mp4_dash,init=").Append(Esc(url(Path.Combine(s.Folder, $"init-stream{st}.m4s")))).Append('\n');
    for (int i = 0; i < chunks.Count; i++)
    {
        int n = chunks[i];
        int units = i == 0 ? n                                           // first part: N0 · 3 s
                  : i + 1 < chunks.Count && chunks[i + 1] != n + 1 ? chunks[i + 1] - n   // before a gap: 3·(next − n) s
                  : 1;                                                   // normal part: 3 s
        sb.Append(Esc(url(Path.Combine(s.Folder, $"chunk-stream{st}-{n:00000}.m4s"))))
          .Append(",length=").Append((units * s.SegmentSeconds).ToString(CultureInfo.InvariantCulture)).Append('\n');
    }
}
static string Esc(string v) => $"%{Encoding.UTF8.GetByteCount(v)}%{v}";
```
(If the first part of a window is also followed by a gap, its length is `N0·3` plus the gap stretch; the golden tests cover that case.
`Window(SessionMedia, positionSeconds, count = 60)` picks the chunk numbers; the reload policy lives in `CacheFollower`.)

### Phase 2: Desktop player MVP (detailed after the S1 verdict; S2 is PARTIAL and its result is applied in §3)
Shell (Mica, title bar, BusyLine, toasts, FastEncoder styles) → `Mpv.cs` (wakeup-driven event loop, **mpv pre-warmed at app start**) →
`MpvD3D11Renderer` → Library page (games rail, Cache strip, grids, filters, search) → Player page (markers lane with icon map,
trickplay filmstrip, transport, keys, resume, A-B loop, fullscreen overlay; every seek or back-step across chunks is a
`loadfile <edl> start=<t>` over the window EDL) → CacheFollower (rebuilds the window EDL and the reload policy) → trickplay
generator (S4: first fragment of each chunk, 160 px JPEG q10 every 3 s, zero-filled chunks skipped) → FSW index → first-run card →
`--selftest`, `--bench`. Markers are placed from Steam's exact millisecond offset (field 1.5.10); the folder-name rule is the fallback.

### Phase 3: Desktop edits, export, delete, network libraries
Cuts, speed/volume/mute, live sync + nudges, **Detect** (FastEncoder `AudioSync`; S6: `Low` confidence on real gameplay, so the UI
shows "Low confidence: not applied" instead of a number, and a fix is `AudioDelay = −AudioLate` through one tested helper), apply to
game, fix timing drift, preset grid (default export preset: TBD, FastEncoder preset ladder under review) + size estimate, export queue with taskbar progress, **Save clip** (markers as chapters), "Open in FastEncoder", highlight reel,
screenshots, colour-range check per encoder, **export folder picker on first export**,
**network libraries** (LinkDetector, ChunkDownloader, Downloads view, optional viewing proxies; download-first everywhere until S3b/S9).

### Phase 4: Android
1. Toolchain + Compose skeleton + brand theme. 2. Server profiles, Keystore, endpoint chooser, encryption switch, connection
info. 3. LinkDetector + ChunkDownloader (WorkManager) + LocalChunkCache. 4. IndexRepository (change notify) + fallback scan,
golden tests. 5. Player over local chunks (same lead EDL and seek-by-reload rule; the phone reads the first-fragment trickplay sheets
the desktop generated); loopback streaming when wired. 6. Editor + AudioSync port (with the same "not applied" path). 7. Export (RangeMuxer +
Transformer, presets, MediaStore, progress notification). 8. Downloads screen (queue, pins, cache size).

### Phase 5: Beyond Steam (each opt-in)
Storage view + batch "compress old clips", cross-library event search, smart cut (re-encode only the GOPs at the cut edges),
achievement names/icons, a highlight reel on the phone. (Linux/macOS clients are out of scope.) Later: S3b + S9 (wired streaming,
link detection), then stream on wired links.

### Phase 6 (last): Delete and clean-up
S10 (needs maintainer approval: writes in Steam's folder) → DeletePolicy (the pure path guard of §4.5: `..`, junctions, UNC, 8.3
names, case variants, non-clip folders and cache sessions are all refused) → DeleteService (§4.5) → "compress old clips" clean-up.

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
| Holding a cache chunk open blocks Steam's cleanup | The cache can't trim | Share-delete opens. S2 measured mpv's own share mode as READ\|WRITE\|DELETE; on copies a held chunk deletes immediately and head trims during playback cause no stall (s2d). Disk space returns only after mpv closes the file, so the window reload matters |
| Steam trims while you watch/export/download | Errors mid-operation | CacheFollower; downloads/exports report "Steam already removed X s" |
| Wireless packet loss | Stutter, corrupt files | Download-first, per-chunk `.part` + size check, the player reads local files only |
| Steam format/config changes | Missing recordings | Golden tests, "unknown layout" diagnostics, never write Steam's folder |
| Delete mistakes | Data loss | Off by default, path guard, Recycle Bin, typed confirmation, audit log, S10 |
| FastEncoder submodule drifts | Build breaks after a FastEncoder change | Submodule pinned to a commit; bump deliberately with `--selftest` green on both projects |
| FastEncoder's shared temp folder cleanup (`CleanupTemp()`) | Rare loss of two-pass logs | SPP never calls it; a proper fix needs a FastEncoder change (maintainer's call) |
| JCIFS on Android | Build/runtime issues | S3; smbj fallback |
| Licence obligations when binaries ship | GPL requires source + licence texts for bundled libmpv/FFmpeg | GPL-3.0-or-later chosen (D19); release packaging adds `docs/third-party.md`, licence texts and the exact libmpv/FFmpeg versions/sources |
| Personal data leaking into the repo | Privacy | Placeholders and synthetic fixtures only; maintainers keep local pre-commit checks |

## 9. Status and open questions
**Phase 0 closed for now (2026-11-07).** Validated: S1, S3, S5, S6, S8. Partial, with the result applied to the plan: S2 (lead
EDL + seek-by-reload), S4 (adaptive first-fragment read), S7 (lossless export passes; 2× re-encode needs a software decoder for
full-range input on the test phone and is tagged BT.601 by its encoder). Not started, deferred: S3b (wired streaming), S9 (link
detection), S10 (Steam clip delete, needs approval). Before Phase 2 / 5 they are run or their features scoped down.

Decisions taken in Phase 0 (2026-11-07): the S8 reference is the C# detector's result (+277 ms); RAM/CPU overhead in S1 is accepted
unless it is a real leak; mobile data gets a toast only (§4.3), with no VPN time budget and no data cap.

Decided 2026-11-07 (start of Phase 1): S3b and S9 later; S10 and delete/clean-up last (Phase 6); FastEncoder submodule = its latest
`main`; bootstrap merged into 1.11 (Phase 2); disconnects and Wi-Fi changes recover on their own with a 30 s timeout (§4.3), which
also covers the AP-roaming question (tested in Phase 4 on the device); 2× export accurate by default with a hidden fast toggle (§4.4).

**Phase 1 in progress.**

Resolved in rev 3.1: FastEncoder is published and used as a submodule (D5); Linux is out of scope; licence GPL-3.0-or-later (D19).
