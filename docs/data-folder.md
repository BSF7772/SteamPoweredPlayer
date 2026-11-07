<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Data folder and `library.json` (schema v1)

The desktop app is the **only writer** (plan D11). Phones and other readers open everything here read-only.
C# reference: `src/Core/Library/{DataFolder,LibraryIndex,IndexStore,Artwork}.cs`. Golden files: `tests/fixtures/golden/library-v1*.json`
(a Kotlin parser must produce the same result for them).

## Location

First run: the desktop proposes a folder named `SteamPoweredPlayer` that is a **sibling** of Steam's `BackgroundRecordPath`
(`D:\Recordings` -> `D:\SteamPoweredPlayer`), so a remote client that sees the recordings through a share sees the data folder
through the same share. The user confirms or picks another folder. Rules (checked on full, normalised paths, case-insensitive,
trailing separators, `.` and `..` resolved; symlinks and junctions are not resolved):

- The folder is never `BackgroundRecordPath` or inside it (we never write into Steam's tree).
- `BackgroundRecordPath` is never inside the folder (we would own Steam's tree).
- `BackgroundRecordPath` at a drive or share root (`D:\`, `\\nas\share`): there is no sibling and `<root>\SteamPoweredPlayer` would
  be inside the recording path, so **no proposal is made**; the user must pick a folder elsewhere (typically another drive).
  Such a library is not reachable through the recordings share; the user has to share the data folder as well.

## Layout

```
<data folder>/
  library-id          one line: a GUID ("D" format, lowercase), created once, never changed
  library.json        the index (below); replaced atomically
  library.json.tmp    transient, writer only; readers ignore it
  scan-cache.json     desktop-only scan cache (below); phones never read it; replaced atomically
  art/<gameId>/       cover.<ext> hero.<ext> heroBlur.<ext> logo.<ext>, copied from Steam's library cache
  trick/              trickplay thumbnails (later phase)
  proxies/            viewing proxies (later phase)
  edits/              saved cut lists (later phase)
```

`library-id` identifies the library: two server profiles with the same id reach the same library (LAN vs VPN address).

## `library.json` v1

UTF-8 without BOM, indented JSON, LF newlines, camelCase names, invariant number format. Writers omit properties whose value is null.

```json
{
  "schemaVersion": 1,
  "libraryId": "11111111-2222-3333-4444-555555555555",
  "generatedUtc": "2025-06-01T08:30:00Z",
  "games": [
    { "gameId": "730", "name": "Test Game", "isShortcut": false,
      "art": { "cover": "art/730/cover.jpg", "hero": "...", "heroBlur": "...", "logo": "..." } }
  ],
  "recordings": [
    { "kind": "cache", "gameId": "730", "startUtc": "2025-01-01T12:00:00Z",
      "relativeFolder": "video/bg_730_20250101_120000",
      "runs": [[1, 40], [45, 60]], "segmentSeconds": 3, "durationSeconds": 171,
      "live": true, "temporary": true, "markerCount": 2, "sizeBytes": 123456789 }
  ]
}
```

| Field | Type | Meaning |
|---|---|---|
| `schemaVersion` | int | 1. Readers compare it first. |
| `libraryId` | string | Same as `library-id`. |
| `generatedUtc` | string | UTC instant `yyyy-MM-ddTHH:mm:ssZ`. |
| `games[].gameId` | **string** | Decimal digits. A string because shortcut ids are 64-bit values above 2^53: JavaScript numbers and some JSON libraries lose precision, and Kotlin needs `ULong`. Parse with `toULong()` only where needed. |
| `games[].name` | string | Display name. |
| `games[].isShortcut` | bool | Non-Steam game (default false). |
| `games[].art.*` | string? | Path **relative to the data folder**, forward slashes. Absent = no art. |
| `recordings[].kind` | string | `cache` (rolling background session), `clip`, `downloadedClip`. Unknown value = reject the file as unreadable (a new kind needs a new `schemaVersion`). |
| `recordings[].gameId` | string? | Absent for `downloadedClip`. References `games[].gameId`; a recording may exist without a games entry (show "Unknown game"). |
| `recordings[].startUtc` | string | UTC instant, from the folder name. |
| `recordings[].relativeFolder` | string | Relative to `BackgroundRecordPath`, forward slashes (`video/bg_...`, `clips/clip_...`). Never contains `..`; readers must refuse it if it does. |
| `recordings[].runs` | `[[first,last],...]` | Complete chunk numbers (inclusive), sorted, non-adjacent. Chunk N lives in `chunk-stream{0,1}-NNNNN.m4s`. |
| `recordings[].segmentSeconds` | number | Chunk length, 3 today (default 3 when absent). |
| `recordings[].durationSeconds` | number | Sum of run lengths x `segmentSeconds`. |
| `live` / `temporary` | bool | Steam is still writing it / part of Steam's rolling cache (it may vanish at any time). |
| `title` | string? | Optional user title. |
| `markerCount` | int | Number of timeline markers (default 0). |
| `exactOffsetMs` | int64? | Measured offset of the audio-sync detector, if known. |
| `sizeBytes` | int64 | Bytes of the recording folder when indexed. |

### Reading rules

1. Read `schemaVersion` first. `< 1` or not a number: corrupt. `> 1`: show "update the app"; try to use the file anyway
   (the C# reader returns a best-effort index when the shape still matches, otherwise none) and never crash.
2. **Ignore unknown properties** at every level.
3. Missing optional properties take the defaults in the table.
4. A corrupt file (not JSON, bad timestamp, bad run) is "unreadable": keep showing the previous in-memory index, or fall back to a
   self-scan of the share.
5. Timestamps are always UTC; parse the trailing `Z`.

### Writing and concurrent access (desktop)

- Serialise first, write `library.json.tmp` in the same folder, flush to disk, then move it over `library.json`
  (one rename; the old file stays intact when anything fails or is cancelled before the rename).
- **Readers open with read + write + delete sharing** so the writer's rename never fails because a reader (a phone over SMB) holds it.
- A reader may still see "not found" or a sharing violation for a few milliseconds while the name is swapped. Retry the open a few
  times (20 ms apart, up to about 3 for not-found, 10 for other I/O errors) before reporting missing/unreadable. The C#
  `IndexStore.ReadAsync` does this; Android readers must too.
- Phones never write here (D11); their own state stays on the phone.

## `art/`

`ArtworkStore.CopyAsync(gameId, sources)` copies each source file once to `art/<gameId>/<role><lowercase ext>` and returns the
relative paths used in the index. A copy is kept while its size and modification time equal the source's (the copy gets the
source's mtime). The source is only read, with read + delete sharing. `gameId` must be decimal digits (it is a folder name).
Missing sources give no entry. Old copies with a different extension are left in place (the desktop never deletes outside the
delete service); the index points to the current one.

## `scan-cache.json` (desktop only, version 1)

Remembers which chunk heads were already checked, so a warm start lists each folder and reads only new or changed files
(a cold scan of ~21 000 chunks took 142 s on a cold disk, 2-3 s warm). Not part of the `library.json` contract: phones ignore it,
and a missing, corrupt or newer-version file just means a cold scan. Written with the same atomic temp-and-swap as `library.json`
(`scan-cache.json.tmp`), at most every few seconds, only after the data folder is confirmed.

```json
{ "version": 1,
  "folders": {
    "video/bg_730_20250101_120000": { "chunks": [ [0, 1, 123456, 638700000000000000, 1], [1, 1, 4567, 638700000000000000, 2] ] }
  } }
```

Key = `recordings[].relativeFolder` (for a clip the clip folder; the chunks are in its media folder). A chunk entry is
`[stream, number, length, lastWriteUtcTicks, head]`, `head` 1 = valid MP4 box, 2 = zero-filled. Only final-name chunk files are stored
(never `.tmp`). An entry is trusted only while the file's name, length and last write time (UTC ticks) match the listing;
anything else is head-read again. Other head results (too short, not a box, unreadable) are never stored.

Since Phase 2.1 a folder entry may also carry an optional `"tfdt": [chunk, length, lastWriteUtcTicks, seconds]`: the real `tfdt`
(seconds, invariant number) of the folder's first video chunk on disk when it was read, with that file's length and last write time.
It places a saved clip on its real time axis (clips restart chunk numbers at 1 but keep the session's tfdt). It is trusted only
while `chunk` is still the first chunk and the file's length and last write time match; otherwise that one fragment is read again.
The key is optional and the file stays version 1: readers that predate it ignore it, and an entry without it is read once.

```json
"clips/clip_730_20250101_120500": { "chunks": [ … ], "tfdt": [1, 456789, 638700000000000000, 585.0] }
```

