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
- Chunks carry absolute `tfdt` (chunk N starts at about N × 3 s), and frames are stamped unevenly (game-capture jitter).

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
segments on demand, `length=3` gives instant seeks without opening every file, and `!new_stream` adds the audio track. Escape values
as `%<utf8-byte-count>%value`. mpv documents `mp4_dash` as "may change at any time", so pin the libmpv version.

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
The mapping from timeline time to video time still has to be validated against visible events (plan spike S5).

## Protobuf files (reverse-engineered, partial)
- `gamerecording.pb`: field 1 repeated `{1 timeline_id, 2 gameid, 3 date_recorded, 4 duration_ms}`. Field 4 repeated is a per-app
  tag index: `{1 appid, 2 tag{1 name, 2 group, 3 icon, 4 priority}, 3[] {2 timeline_id, 3 offset_ms}}`. Field 2 is unknown.
- `clip.pb`: `{1 {1 timeline_id, 2 gameid, 3 date, 4 duration_ms, 5 {1 bg_folder, 2 start_ms, 3 duration_ms, …}}, 3 created, 4 gameid,
  7 title, 12 width, 13 height, …}`.
- Related message names: SteamTracking/Protobufs `webui/service_gamerecordingclip.proto` (`CMsgVideoGameRecordingDef`).
  Validate any schema against many real files, never one.
