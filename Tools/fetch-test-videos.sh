#!/usr/bin/env bash
set -euo pipefail
output_dir="${1:-/tmp/bodyoracle-test-videos}"
mkdir -p "$output_dir"
# Official TensorFlow.js pose detector test fixtures. Keep downloaded media out of Assets/builds.
for video in pose_1 pose_squats; do
  curl -L --fail "https://raw.githubusercontent.com/tensorflow/tfjs-models/master/pose-detection/test_data/$video.mp4" \
    -o "$output_dir/$video.mp4"
done
printf 'Downloaded videos to %s\n' "$output_dir"
