#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p Logs
case "${1:-}" in
  mac)
    : > Logs/build-mac.log
    unity build . "${@:2}" --profile Mac --output-path Builds/Mac/BodyOracle.app --log-file Logs/build-mac.log
    grep -Fq 'Build Finished, Result: Success.' Logs/build-mac.log
    test -f Builds/Mac/BodyOracle.app/Contents/Info.plist
    bash Tools/sign-mac-local.sh Builds/Mac/BodyOracle.app
    ;;
  ios)
    : > Logs/build-ios.log
    unity build . "${@:2}" --profile iOS --output-path Builds/iOS --log-file Logs/build-ios.log
    grep -Fq 'Build Finished, Result: Success.' Logs/build-ios.log
    test -f Builds/iOS/Unity-iPhone.xcodeproj/project.pbxproj
    ;;
  *) printf 'Usage: bash Tools/build.sh mac|ios [Unity CLI build options]\n' >&2; exit 2 ;;
esac
