<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Third-party components

SteamPoweredPlayer is licensed GPL-3.0-or-later (see `LICENSE`). The release bundles the binaries below next to the executable.
They are never committed to this repository: `tools/deps.json` pins each one (download URL, size, SHA-256), `tools/bootstrap.ps1`
fetches and verifies them into the gitignored `.tools/`, and `tools/Dependencies.targets` re-checks the libmpv hash on every build
and copies the binaries and `licenses/` to the output. A wrong hash fails the build.

## Bundled binaries

| Component | Version (pinned in `tools/deps.json`) | Used for | Licence of the bundled build | Bundled |
|---|---|---|---|---|
| libmpv (`libmpv-2.dll`) | mpv 0.42.0, shinchiro mpv-winbuild-cmake release 20261105 (`3138fc8eea`) | Playback (D3D11 render API) | GPL-2.0-or-later build, distributed as part of a GPL-3.0-or-later work | always |
| ffmpeg, ffprobe | FFmpeg 8.1.3 (+34 commits, `a35157abb9`), BtbN FFmpeg-Builds autobuild-2026-11-06-12-50, win64 GPL static | Lossless range export, encoding, analysis | GPL (x264, x265 and other GPL components enabled; `ffmpeg -L` prints the exact text) | release builds; optional for development (ffmpeg >= 8 on PATH is accepted) |

Development-only tools (not shipped): Temurin JDK 21.0.13+9 (GPL-2.0 with Classpath exception), Gradle 9.9.0 (Apache-2.0), Android
command-line tools and platform-tools (Android SDK licence / Apache-2.0). They are pinned in the same manifest, group `android`.

## Source offer
The libmpv and ffmpeg binaries shipped with SteamPoweredPlayer are covered by the GNU General Public License. The complete
corresponding source code of the exact versions shipped is available from the upstream projects, at the commits below, and from
the build recipes that name every library linked into the binaries:

| Binary | Exact source | Build recipe |
|---|---|---|
| libmpv 0.42.0 | https://github.com/mpv-player/mpv/tree/3138fc8eea | https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261105 |
| ffmpeg, ffprobe 8.1.3-34 | https://github.com/FFmpeg/FFmpeg/tree/a35157abb9 | https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-11-06-12-50 |

The source of the statically linked libraries (libass, libplacebo, x264, x265, SVT-AV1, and so on) is referenced by those recipes at
the versions they were built with. If you received a SteamPoweredPlayer release and cannot obtain that source, open an issue in this
repository and the source for the shipped versions will be provided for as long as the release is distributed, at no more than the
cost of physically performing the distribution.

How to rebuild the binaries: follow the recipe repository above (both are cross-compiled with their documented Docker/CMake
setups), then either replace the entry in `tools/deps.json` (URL, size, SHA-256, and the extracted DLL hash) or point the build at
your own build with `-p:LibMpvDir=<folder with libmpv-2.dll> -p:LibMpvSha256=<its hash>`. SteamPoweredPlayer itself can be built
against a modified libmpv or ffmpeg this way.

The licence texts and pointers ship in the release's `licenses/` folder (see `licenses/README.md`). The source of SteamPoweredPlayer
itself is this repository.

## NuGet dependencies (desktop)

| Package | Version | Licence | Used by |
|---|---|---|---|
| Microsoft.WindowsAppSDK | 1.8.260921001 | MIT | WinUI 3 app |
| Vortice.Direct3D11, Vortice.DXGI (and their Vortice.* dependencies) | 3.8.3 | MIT | D3D11 renderer |
| Google.Protobuf | see `tests/*.csproj` | BSD-3-Clause | tests only (building fixtures); not shipped |
| xUnit and the .NET test SDK | see `tests/*.csproj` | Apache-2.0 / MIT | tests only; not shipped |

## Gradle dependencies (Android)

Declared in `android/app/build.gradle.kts`, resolved by Gradle from Google Maven and Maven Central. Besides the Jetpack libraries:

| Package | Version | Licence | Used by |
|---|---|---|---|
| `dev.jdtech.mpv:libmpv` | 1.0.0 (mpv 0.42.0, FFmpeg 9.0.2; ABIs arm64-v8a, armeabi-v7a, x86, x86_64; the app packages arm64-v8a and x86_64). AAR SHA-256 `df146592480fc8418415a06b1f1a1d6318b0088e21f52254b0e9a82b61ca8fa2` | Wrapper MIT; the binaries GPL-3.0-or-later as a whole | Player. Source and build scripts: https://github.com/jarnedemeulemeester/libmpv-android |
| `androidx.media3:media3-muxer`, `-transformer`, `-effect`, `-common` | 1.11.1 | Apache-2.0 | Lossless export, re-encodes |
| `io.coil-kt.coil3:coil-compose` | 3.3.0 (newer ones need compileSdk 37) | Apache-2.0 | Game art, trickplay sheets |
| `org.codelibs:jcifs` | 3.0.4 | LGPL-2.1 | SMB |

Other dependencies (further NuGet and Gradle packages) keep their own licences; they are compatible with GPL-3.0-or-later (see plan
§2 D19). Update this table whenever a dependency is added.
