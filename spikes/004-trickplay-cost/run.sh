#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Spike S4 measurement matrix. Usage: run.sh <SessionDir> [tag]   (SessionDir = a video/bg_* folder, opened read-only)
# Run from the spike folder in git-bash. Results: out/<tag>_*.txt. Use Windows-style paths (X:/...) for <SessionDir>.
set -u
S="$1"; TAG="${2:-run}"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
EXE="dotnet bin/Release/net10.0/Trickplay.Spike.dll"
mkdir -p out
run() { local name="$1"; shift; echo "### $name: $*" | tee "out/${TAG}_${name}.txt"; $EXE "$@" 2>&1 | tee -a "out/${TAG}_${name}.txt"; echo; }

run scan            scan "$S" "out/${TAG}_scan.csv" 2097152
# per-chunk decode timing: raw 320x180 frames to a pipe, every chunk of the session
run d3d11va_adapt   decode "$S" d3d11va 0       raw "out/${TAG}_o1"      # first run = cold file cache
run d3d11va_adapt2  decode "$S" d3d11va 0       raw "out/${TAG}_o2"      # warm
run d3d11va_512k    decode "$S" d3d11va 524288  raw "out/${TAG}_o3"
run d3d11va_1m      decode "$S" d3d11va 1048576 raw "out/${TAG}_o4"
run cuda_adapt      decode "$S" cuda    0       raw "out/${TAG}_o5"
run cudascale_adapt decode "$S" cuda-scale 0    raw "out/${TAG}_o6"
run cpu_adapt       decode "$S" cpu     0       raw "out/${TAG}_o7"
run cpu_1thread     decode "$S" cpu     0       raw "out/${TAG}_o8" 0 1 1
# sprite sheets 10x10, every chunk
run sheets_jpg      decode "$S" d3d11va 0       jpg  "out/${TAG}_sheets_jpg"
run sheets_webp     decode "$S" d3d11va 0       webp "out/${TAG}_sheets_webp"
