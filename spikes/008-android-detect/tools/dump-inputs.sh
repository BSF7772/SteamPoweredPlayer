#!/usr/bin/env bash
# Dumps exactly what FastEncoder's AudioSync.cs feeds its analysis (same ffmpeg arguments), for the Kotlin port check.
# Usage: dump-inputs.sh <media> <outdir> [start] [len]   (len <= 75; DetectAsync's middle-75 s clamp is the caller's job)
set -euo pipefail
in="$1"; out="$2"; ss="${3:-0}"; len="${4:-75}"; mkdir -p "$out"
ffmpeg -hide_banner -nostdin -loglevel error -ss "$ss" -t "$len" -i "$in" -map 0:a:0 -vn \
  -af aresample=async=1:first_pts=0 -ac 1 -ar 16000 -f f32le "$out/audio.f32"
ffmpeg -hide_banner -nostdin -loglevel info -hwaccel auto -ss "$ss" -t "$len" -i "$in" -map 0:v:0 -an \
  -vf scale=192:108:flags=area,format=gray,showinfo -fps_mode passthrough -f rawvideo "$out/video.gray" 2> "$out/video.log"
grep -a 'pts_time:' "$out/video.log" > "$out/video.pts"
echo "audio $(( $(stat -c %s "$out/audio.f32") / 4 )) samples, video $(wc -l < "$out/video.pts") frames"
