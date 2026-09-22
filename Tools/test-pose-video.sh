#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "$#" -lt 1 ]; then
  printf 'Usage: bash Tools/test-pose-video.sh path/to/video.mp4 [results-directory]\n' >&2
  exit 2
fi
video_path="$1"
results_path="${2:-Logs/pose-video}"
frames_root="$(mktemp -d "${TMPDIR:-/tmp}/bodyoracle-video.XXXXXX")"
mkdir -p "$frames_root/video" "$results_path"
ffmpeg -y -i "$video_path" "$frames_root/video/frame-%04d.png" -loglevel error
ffprobe -v error -show_entries format=duration -show_entries stream=width,height,avg_frame_rate -of json "$video_path" > "$results_path/source.json"
shasum -a 256 "$video_path" > "$results_path/source.sha256"
unity run . --editor-version 6000.3.24f1 --timeout 1200 -- \
  -executeMethod BodyOracleVideoValidation.Run -poseFrames "$frames_root" \
  -poseResults "$results_path" -logFile "$results_path/unity.log"
printf 'Results: %s\nDecoded test frames retained at: %s\n' "$results_path" "$frames_root"
