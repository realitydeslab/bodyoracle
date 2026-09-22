#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p Logs
unity run . --editor-version 6000.3.24f1 --timeout 600 -- \
  -executeMethod BodyOracleGpuValidation.Run -logFile Logs/gpu-validation.log
