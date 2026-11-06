# Third-party components

SteamPoweredPlayer is licensed GPL-3.0-or-later (see `LICENSE`). The release bundles the binaries below next to the executable.
They are never committed to this repository: a pinned manifest (download URL + SHA-256) lives in the repo, and a build step fetches
and verifies them.

| Component | Used for | Licence of the bundled build |
|---|---|---|
| libmpv | Playback (D3D11 render API on Windows) | GPL-3.0-or-later build |
| ffmpeg | Lossless range export, encoding, analysis | GPL-2.0-or-later build (with GPL-only encoders such as x264 and x265 enabled) |

Versions: see the manifest.

## Source offer
The libmpv and ffmpeg binaries shipped with SteamPoweredPlayer are covered by the GNU General Public License. The complete
corresponding source code of the exact versions shipped is available from the upstream projects (mpv: https://github.com/mpv-player/mpv,
FFmpeg: https://ffmpeg.org/) and from the build recipe linked in the manifest entry of each binary. If you received a
SteamPoweredPlayer release and cannot obtain that source, open an issue in this repository and the source for the shipped versions
will be provided for as long as the release is distributed, at no more than the cost of physically performing the distribution.

The licence texts of the bundled components ship in the release's `licenses/` folder. The source of SteamPoweredPlayer itself is
this repository.

Other dependencies (NuGet and Gradle packages) keep their own licences; they are compatible with GPL-3.0-or-later (see plan §2 D19).
