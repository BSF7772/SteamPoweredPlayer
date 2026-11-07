#!/usr/bin/env bash
# S7 desktop-side checks of the phone's exports. Usage: verify.sh <source video (fMP4)> <source audio> <export.mp4> [shift_ms]
# 1) video packets bit-identical and in the same order (framemd5 of stream copy), PTS preserved up to a constant offset
# 2) audio start offset vs video, 3) colour range as ffprobe sees it + colr box, 4) B-frame table (ctts) present
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
src_v="$1"; src_a="$2"; out="$3"; shift_ms="${4:-300}"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

ffmpeg -v error -i "$src_v" -map 0:v:0 -c copy -f framemd5 "$tmp/src.md5"
ffmpeg -v error -i "$out" -map 0:v:0 -c copy -f framemd5 "$tmp/out.md5"
# columns: stream, dts, pts, duration, size, hash -> compare size+hash in order, and pts deltas
cut -d, -f5,6 "$tmp/src.md5" | grep -v '^#' > "$tmp/s.h"; cut -d, -f5,6 "$tmp/out.md5" | grep -v '^#' > "$tmp/o.h"
n_src=$(wc -l < "$tmp/s.h"); n_out=$(wc -l < "$tmp/o.h")
same=$(head -n "$n_out" "$tmp/s.h" | cmp -s - "$tmp/o.h" && echo yes || echo no)
echo "video packets: source $n_src, export $n_out, identical prefix (size+md5, decode order): $same"

ffprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 "$src_v" | head -n "$n_out" > "$tmp/s.pts"
ffprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 "$out" > "$tmp/o.pts"
paste -d' ' "$tmp/s.pts" "$tmp/o.pts" | awk 'NR==1{d=$1-$2} {e=($1-$2)-d; if(e<0)e=-e; if(e>m)m=e} END{printf "video PTS preserved up to a constant offset: max deviation %.6f s over %d packets\n", m, NR}'

ffprobe -v error -show_entries stream=index,codec_type,start_time,duration,color_range,color_space,has_b_frames -of compact "$out"
vs=$(ffprobe -v error -select_streams v:0 -show_entries stream=start_time -of csv=p=0 "$out")
as=$(ffprobe -v error -select_streams a:0 -show_entries stream=start_time -of csv=p=0 "$out" || echo "")
sv=$(ffprobe -v error -select_streams v:0 -show_entries stream=start_time -of csv=p=0 "$src_v")
sa=$(ffprobe -v error -select_streams a:0 -show_entries stream=start_time -of csv=p=0 "$src_a")
awk -v vs="$vs" -v as="$as" -v sv="$sv" -v sa="$sa" -v sh="$shift_ms" 'BEGIN{
  src=(sa-sv)*1000; exp=src+sh; got=(as-vs)*1000;
  printf "audio-video start: source %+.1f ms, expected %+.1f ms, export %+.1f ms, error %+.1f ms\n", src, exp, got, got-exp }'
python3 "$here/boxes.py" "$out"
