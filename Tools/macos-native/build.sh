#!/usr/bin/env bash
# Build the macOS native plug-in (zero-copy camera, full-screen menu) into Packages/org.realitydeslab.body-oracle/Plugins/macOS/BodyOracleMac.bundle.
set -euo pipefail
cd "$(dirname "$0")/../.."
unity_version="$(awk '/m_EditorVersion:/ {print $2}' ProjectSettings/ProjectVersion.txt)"
plugin_api="${UNITY_PLUGIN_API:-/Applications/Unity/$unity_version/Unity.app/Contents/PluginAPI}"
bundle="Packages/org.realitydeslab.body-oracle/Plugins/macOS/BodyOracleMac.bundle"
mkdir -p "$bundle/Contents/MacOS"
xcrun clang++ -std=c++17 -fobjc-arc -bundle -O2 -Wall -Werror \
  -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 \
  -I "$plugin_api" \
  -framework Foundation -framework AppKit -framework AVFoundation -framework CoreMedia -framework CoreVideo -framework Metal \
  -o "$bundle/Contents/MacOS/BodyOracleMac" Tools/macos-native/BodyOracleCamera.mm Tools/macos-native/BodyOracleMenu.mm
cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>BodyOracleMac</string>
  <key>CFBundleIdentifier</key><string>org.realitydeslab.body-oracle.native</string>
  <key>CFBundlePackageType</key><string>BNDL</string>
  <key>CFBundleName</key><string>BodyOracleMac</string>
  <key>CFBundleVersion</key><string>1</string>
</dict>
</plist>
PLIST
codesign --force --sign - "$bundle"
printf 'Built %s\n' "$bundle"
