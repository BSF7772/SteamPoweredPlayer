<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Licence texts shipped with SteamPoweredPlayer

This folder is copied to `licenses\` next to the executable in every build and release (by `tools/Dependencies.targets`).

| File in the release | What it is | Source |
|---|---|---|
| `GPL-3.0.txt` | Full text of the GNU GPL v3, the licence of SteamPoweredPlayer and of the bundled GPL builds | the repository's `LICENSE` file, copied at build time |
| `mpv.md` | Licence and source pointers for libmpv | this folder |
| `FFmpeg.md` | Licence and source pointers for ffmpeg | this folder |
| `FFmpeg-LICENSE.txt` | The licence file that comes inside the ffmpeg build | copied from the installed ffmpeg archive when ffmpeg is bundled |
| `third-party.md` | Components, versions, the GPL source offer | `docs/third-party.md`, copied at build time |

No binaries live here. The exact versions, download URLs and SHA-256 hashes of the bundled binaries are in `tools/deps.json`.
The full text of each upstream licence is reachable from the pointer files; they are fetched documents of the upstream
projects, not copies made by us.
