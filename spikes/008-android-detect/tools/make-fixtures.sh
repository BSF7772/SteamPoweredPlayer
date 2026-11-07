#!/usr/bin/env bash
# FastEncoder's synthetic "sound 300 ms late" clip (SelfTest.cs recipe), at several durations. Usage: make-fixtures.sh <outdir> [durations...]
set -euo pipefail
out="$1"; shift; mkdir -p "$out"
flips="mod(floor(T*1.3)+floor(T*0.77+0.2),2)"
clicks="lt(mod((t-0.3)*1.3,1),0.013)+lt(mod((t-0.3)*0.77+0.2,1),0.008)"
for d in "${@:-10 14 75}"; do
  ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "color=c=black:s=320x180:r=60:d=$d" \
    -f lavfi -i "aevalsrc='0.8*min(1,$clicks)*sin(2*PI*2500*t)+0.01*(random(0)-0.5)':s=48000:d=$d" \
    -vf "geq=lum='16+219*$flips':cb=128:cr=128" \
    -c:v libx264 -preset ultrafast -crf 18 -g 30 -c:a aac -b:a 128k -shortest "$out/sync_late300_${d}s.mp4"
done
