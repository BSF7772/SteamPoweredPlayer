<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# FFmpeg

- Bundled files (when ffmpeg is bundled): `ffmpeg.exe`, `ffprobe.exe` from the BtbN FFmpeg-Builds "GPL" static build
  `n8.1.3-34-ga35157abb9` (release `autobuild-2026-11-06-12-50`).
- Licence: GPL. The build enables GPL components (for example x264 and x265). `ffmpeg -L` prints the exact licence of the
  binary, and `FFmpeg-LICENSE.txt` is the file that ships inside the archive.
  Licence text: https://github.com/FFmpeg/FFmpeg/blob/a35157abb9/COPYING.GPLv3
- Source of the exact version: https://github.com/FFmpeg/FFmpeg/tree/a35157abb9
- Build recipe (includes the versions and sources of every linked library): https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-11-06-12-50
