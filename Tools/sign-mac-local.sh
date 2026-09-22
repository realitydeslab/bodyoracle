#!/usr/bin/env bash
# Repair the copied App UI native plug-in's signature for a local ad-hoc build.
# Developer ID signing and notarization belong to a distribution workflow.
set -euo pipefail
app_path="${1:-Builds/Mac/BodyOracle.app}"
signature_info="$(codesign -d --verbose=2 "$app_path" 2>&1)"
if [[ "$signature_info" != *"Signature=adhoc"* ]]; then
  printf 'Refusing to replace a non-ad-hoc application signature: %s\n' "$app_path" >&2
  exit 1
fi
# Unity CLI writes provenance next to Contents, which codesign rejects as an
# unsealed bundle-root file. Retain it as a sidecar outside the application.
if [[ -f "$app_path/unity-build.provenance.json" ]]; then
  mv "$app_path/unity-build.provenance.json" "$app_path.provenance.json"
fi
plugin_path="$app_path/Contents/PlugIns/libAppUINativePlugin.dylib"
if [[ -f "$plugin_path" ]] && ! codesign --verify --strict "$plugin_path" 2>/dev/null; then
  codesign --force --sign - --preserve-metadata=entitlements "$plugin_path"
fi
codesign --force --sign - --preserve-metadata=entitlements "$app_path"
codesign --verify --deep --strict --verbose=2 "$app_path"
