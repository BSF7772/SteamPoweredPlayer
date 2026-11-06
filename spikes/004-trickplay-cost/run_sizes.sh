#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Spike S4: sprite-sheet size variants (every chunk, GPU decode). Usage: run_sizes.sh <SessionDir> [tag]
S="$1"; TAG="${2:-run}"; EXE="dotnet bin/Release/net10.0/Trickplay.Spike.dll"
v() { local name="$1" w="$2" fmt="$3" q="$4"; echo "### $name (THUMB_W=$w fmt=$fmt quality=$q)" | tee "out/${TAG}_$name.txt"
  THUMB_W=$w JPG_Q=$q WEBP_Q=$q $EXE decode "$S" d3d11va 0 "$fmt" "out/${TAG}_$name" 2>&1 | grep -a -E "^wall|sheets=|sprite" | tee -a "out/${TAG}_$name.txt"; }
v jpg320_q5 320 jpg 5; v jpg320_q10 320 jpg 10; v jpg240_q8 240 jpg 8; v jpg160_q5 160 jpg 5; v jpg160_q10 160 jpg 10
v webp320_q40 320 webp 40; v webp240_q50 240 webp 50; v webp160_q50 160 webp 50
