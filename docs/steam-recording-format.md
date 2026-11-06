# Steam Game Recording: on-disk format

What SteamPoweredPlayer relies on, as observed on Windows Steam clients in 2025–2026. Steam doesn't document this format, so
treat every rule here as an observation: the scanner must flag layouts it doesn't recognise instead of guessing.

## Where it lives
Steam's settings are in `<Steam>/userdata/<steam-user-id>/config/localconfig.vdf`, block `"GameRecording"`. Read **only** this
block; the file holds unrelated keys.

| Key | Example | Meaning |
|---|---|---|
| `BackgroundRecordMode` | `1` | 1 = background recording on |
| `BackgroundMaxKeep` | `120min` | Length of the per-game background cache (configurable up to 24 h) |
| `BackgroundRecordPath` | `D:\SteamRecordings` | Root of everything below |
| `InstantClipDuration` | `60` | Seconds saved by the instant-clip hotkey |
| `ExportDirectory` | — | Where Steam itself exports MP4s (not used as our default) |
| `PerGameSettings.<appid>.enabled` | `0` | Recording disabled for that game |

Steam's install folder: `HKCU\Software\Valve\Steam\SteamPath`. The active user: `config/loginusers.vdf` → `MostRecent`.

## Layout under `BackgroundRecordPath`
```
video/bg_<gameid>_<yyyyMMdd>_<HHmmss>/        background cache of one play session
clips/clip_<gameid>_<yyyyMMdd>_<HHmmss>/      saved clip: clip.pb, thumbnail.jpg, timelines/*.json, video/bg_*/ (its own chunks)
clips/clip_downloaded_<yyyyMMdd>_<HHmmss>/    clip received from another device
timelines/timeline_<gameid><yyyyMMdd>_<HHmmss>.json   (no separator between gameid and date)
gamerecording.pb                              Steam's index (protobuf), plus stray *.async*.tmp files
```
Regexes: `^(bg|clip)_(\d+)_(\d{8})_(\d{6})$`, `^clip_downloaded_(\d{8})_(\d{6})$`, `^timeline_(\d+)(\d{8})_(\d{6})\.json$`.

**All timestamps in names are UTC.** `timeline_73020250101_120000.json` has `"daterecorded": "1735732800"`
(2025-01-01T12:00:00Z), while a clip's title (`clip.pb` field 7) shows local time.

## Background cache vs. clips vs. recordings
- **Background cache** (`video/bg_*`): a per-game rolling cache of the last `BackgroundMaxKeep`. Steam trims it continuously from
  the oldest end as more is recorded, and whole old sessions disappear. That's by design. The player treats it as temporary,
  follows trims and growth, and offers "Save clip" to keep footage. The exact eviction rule is Steam's; don't predict it.
- **Clips** (`clips/clip_*`): permanent until the user deletes them. Each clip carries its own copy of the chunks.
- **On-demand recordings** (manual start/stop): permanent. No sample has been inspected yet, so the scanner reports unknown
  layouts instead of guessing.

**Never block Steam's cleanup:** open every file under `BackgroundRecordPath` with read + write + delete sharing
(`FileShare.ReadWrite | FileShare.Delete` on Windows, the same share access over SMB).

## Media
- MPEG-DASH written by ffmpeg's dash muxer (Steam ships libavformat): `session.mpd`, `init-stream0.m4s` (video),
  `init-stream1.m4s` (audio), `chunk-stream{0,1}-NNNNN.m4s`, 3.000 s per segment.
- Typical video: HEVC Main (`hev1`), 1080p–1440p at 59.94 fps, ~22 Mbps, **`yuvj420p` (full range)**. Older or other GPUs can give
  H.264 (`avc1`). Audio: AAC-LC 48 kHz stereo, 128 kbps.
- **One IDR at the start of each chunk** (GOP = 3 s). Lossless cuts snap to 3 s; frame-exact cuts need a re-encode.
- Chunks carry absolute `tfdt`, and frames are stamped unevenly (game-capture jitter). **The `tfdt` axis is the session's absolute
  time**: chunk N starts at about (N−1) × 3 s (+ up to 8 ms) in live sessions and also in trimmed sessions (a session whose chunk
  1881 sits at 5640.006 s). Clips restart the chunk numbers at 1 but **not** `tfdt` (chunk 1 of one clip starts at 585.0 s).
  Playback time, markers and the trickplay all use this axis (plan §3).
- **Layout of a video chunk (one fragment per frame):** each `moof` (108 B) is followed by an `mdat` with one frame, and `tfhd` carries
  the defaults. The first fragment holds `VPS SPS PPS SEI IDR_W_RADL` (the IDR), so **the first fragment alone is enough to decode a
  thumbnail**. IDR sizes seen: 1 KB to 906 KB, median 247–359 KB, p99 570–689 KB (so no fixed 512 KB prefix is safe: a fixed 512 KiB
  read truncated 14.9 % of the IDRs in the S4 sample).
- **Zero-filled chunks exist** (S4 found two at the end of one session, with normal chunk names). Check the head of each chunk (an 8-byte
  read: a valid chunk starts with an MP4 box header, a zero-filled one with zeros) before trusting it.

## The manifest is not the truth
Seen in practice:
- `startNumber="1"` while the first chunk on disk is `01840`, with `mediaPresentationDuration="PT0.0S"` (a trimmed cache session).
- `session.mpd` made entirely of zero bytes, next to `.tmp` chunks (a crashed recording).
- `type="dynamic"` with `.tmp` chunks while a session is being recorded.

ffmpeg's DASH demuxer fails on these ("Error when loading first fragment of playlist"). A rewritten MPD opens, but its seeks fail,
and `<BaseURL>file:///…` isn't honoured. **Don't build playback on the MPD.**

Truth = files on disk: segment N is complete when `chunk-stream0-N.m4s` and `chunk-stream1-N.m4s` both exist without `.tmp`.
Group complete numbers into contiguous runs, because gaps are possible. Old `.tmp` files are crash leftovers. A session is live
when its manifest is `dynamic` and its newest chunk is only seconds old.

## Byte-concatenation (lossless, works on broken manifests)
`init-stream0.m4s + chunk-stream0-A … B` is a valid fragmented MP4, and the same goes for audio with stream 1. Fragmented MP4 is
streamable, so it can be piped:
```bash
cat init-stream1.m4s chunk-stream1-0{A..B}.m4s > a.m4a
cat init-stream0.m4s chunk-stream0-0{A..B}.m4s | ffmpeg -v error -i pipe:0 -i a.m4a \
    -map 0:v -map 1:a -c copy -avoid_negative_ts make_zero -movflags +faststart -y cut.mp4
```
The result is in sync and starts at 0. A 30 s remux takes well under a second on an SSD.

## Playback with mpv (EDL)
mpv's EDL can play the chunks directly: `!mp4_dash,init=<init>` prefixes the init segment to each chunk at the byte level and opens
segments on demand and `!new_stream` adds the audio track. Escape values as `%<utf8-byte-count>%value`. mpv documents `mp4_dash` as
"may change at any time", so pin the libmpv version.

What S2 found with real sessions:
- **Do not give every segment `length=3`.** Seeks then land early, audio can be lost and a gap freezes. Use the **lead EDL**: the first
  part is `N0 × 3 s` long and the part before each gap is stretched over the gap, so mpv's `time-pos` equals the `tfdt` media time.
- **Seek = reload:** every seek or back-step across chunks is `loadfile <edl> start=<t>`, over a window of about 60 chunks around the
  playhead (RAM grows with the media length, so the window is rebuilt and the file is reloaded periodically). The measured reload
  median is 95 ms with hardware decode.
- mpv opens the chunk files with share mode READ | WRITE | DELETE. Whether Steam's own cleanup still succeeds while mpv holds a chunk
  open was **not tested**.

## Game IDs, names, art
- `gameid <= 2^32`: a Steam appid (e.g. `730`).
- Larger: a **non-Steam shortcut**, `gameid = (shortcutAppId << 32) | 0x02000000`. Example: shortcut appid `2400000000` →
  gameid `10307921510433554432`. The `appid` and `AppName` come from `<Steam>/userdata/<id>/config/shortcuts.vdf` (binary KeyValues).
- Steam app names: `steamapps/appmanifest_<appid>.acf` in every library listed in `steamapps/libraryfolders.vdf`, then
  `appcache/appinfo.vdf` for uninstalled apps.
- Art: `appcache/librarycache/<appid>/library_600x900.jpg`, `library_hero.jpg`, `library_hero_blur.jpg`, `logo.png`. Shortcut art is in
  `userdata/<id>/config/grid/`. Copy it once into the app's data folder so the app keeps working without Steam.

## Timeline JSON
`daterecorded` (unix s), `starttime`/`endtime` (ms), `entries[]`: `id`, `time` (ms), `type` ∈ {`event`, `state_description`,
`gamemode`, `screenshot`, `achievement`, `phase`, `error`}, `title`, `description` (may contain emoji; read as UTF-8), `icon` (e.g.
`cs2_death`, `cs2_gun_kill`, `cs2_bomb_plant`, `steam_screenshot`), `priority`, `duration`, `possible_clip`, `mode` (game-mode bands),
`tags[{name,group,icon,priority}]` (e.g. Map / Mode on phases), `achievement_name`, `handle` (screenshots).
**Mapping to video time (validated, spike S5):** `position = entry.time / 1000 − offsetMs / 1000` is the absolute session time, equal
to the media `tfdt` (not an index into the chunks on disk). `offsetMs` is Steam's exact value (protobuf field 1.5.10, below). The
fallback from the folder names, `(bgStartUtc − timelineStartUtc) × 1000`, is quantised to whole seconds, so it is only good to
±1 s (measured −0.61 … +0.59 s over 18 sessions). With the exact offset a visible event starts +0.09 s after the mapped time
(range −0.15 … +0.30 s, 11 fine readings). `daterecorded` always equals the UTC time in the timeline file name. A timeline can start up
to about 113 s before its bg folder. Composite events (`cs2_double_kill`, `cs2_multi_kill`) have `duration` ≈ 8 s and are ranges
(the first kill is about 2 s after `time`); single `cs2_death`, `cs2_gun_kill`, `cs2_knife_kill`, `cs2_taser_kill` have `duration 0`.

## Protobuf files (reverse-engineered, partial)
- `gamerecording.pb`: field 1 repeated `{1 timeline_id, 2 gameid, 3 date_recorded, 4 duration_ms}`. Field 4 repeated is a per-app
  tag index: `{1 appid, 2 tag{1 name, 2 group, 3 icon, 4 priority}, 3[] {2 timeline_id, 3 offset_ms}}`. Field 2 is unknown.
  **Field 1.5.10 is the exact offset (ms) of the bg-session start inside the timeline** (e.g. `57332` where the folder names say 57 s).
  Field 1.5 exists only for sessions whose cache folder is still present. Field 1.5.2 is *not* the offset for a trimmed session
  (there it is field 10 + the trimmed time), and 1.5.3 is the retained duration in ms: use field 10.
- `clip.pb` (the offset is field 1 → 5 → **10**, same meaning and unit): `{1 {1 timeline_id, 2 gameid, 3 date, 4 duration_ms, 5 {1 bg_folder, 2 start_ms, 3 duration_ms, …}}, 3 created, 4 gameid,
  7 title, 12 width, 13 height, …}`.
- Related message names: SteamTracking/Protobufs `webui/service_gamerecordingclip.proto` (`CMsgVideoGameRecordingDef`).
  Validate any schema against many real files, never one.
