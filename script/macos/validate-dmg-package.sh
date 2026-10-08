#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DMG_PATH="${1:?DMG path is required.}"
EXPECTED_VERSION="${2:?Expected release version is required.}"
EXPECTED_RUNTIME_IDENTIFIER="${3:?Expected runtime identifier is required.}"
EXPECTED_MANIFEST_PATH="${4:-}"
MOUNT_POINT="$(mktemp -d "${TMPDIR:-/tmp}/downkyi-dmg.XXXXXX")"
COPY_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/downkyi-installed-app.XXXXXX")"
ATTACHED=false

cleanup() {
  if [ "$ATTACHED" = "true" ]; then
    hdiutil detach "$MOUNT_POINT" -quiet || hdiutil detach "$MOUNT_POINT" -force -quiet || true
  fi
  rmdir "$MOUNT_POINT" 2>/dev/null || true
  rm -rf -- "$COPY_ROOT"
}
trap cleanup EXIT

validate_app_boundary() {
  local app_path="$1"
  local boundary="$2"

  echo "[INFO] Validating $boundary app boundary: $app_path"
  "$SCRIPT_DIR/verify-runtime-architecture.sh" "$app_path" "$EXPECTED_RUNTIME_IDENTIFIER"
  "$SCRIPT_DIR/verify-app-signature.sh" "$app_path"
  /bin/bash "$SCRIPT_DIR/aria2-runtime-integrity.sh" verify "$app_path"
  /bin/bash "$SCRIPT_DIR/verify-app-loader.sh" "$app_path"
  /usr/bin/xcrun swift "$SCRIPT_DIR/verify-app-bundle-launch.swift" "$app_path"
  /bin/bash "$SCRIPT_DIR/verify-aria2-rpc-readiness.sh" "$app_path"
}

hdiutil attach -readonly -nobrowse -mountpoint "$MOUNT_POINT" "$DMG_PATH" >/dev/null
ATTACHED=true

APP_PATH="$(find "$MOUNT_POINT" -maxdepth 1 -type d -name '*.app' -print -quit)"
if [ -z "$APP_PATH" ]; then
  echo "::error::The mounted DMG does not contain an app bundle." >&2
  exit 1
fi

PLIST_PATH="$APP_PATH/Contents/Info.plist"
SHORT_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$PLIST_PATH")"
BUNDLE_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$PLIST_PATH")"
if [ "$SHORT_VERSION" != "$EXPECTED_VERSION" ] || [ "$BUNDLE_VERSION" != "$EXPECTED_VERSION" ]; then
  echo "::error::Mounted app bundle version does not match $EXPECTED_VERSION (short=$SHORT_VERSION, bundle=$BUNDLE_VERSION)." >&2
  exit 1
fi

if [ -n "$EXPECTED_MANIFEST_PATH" ]; then
  pwsh -NoLogo -NoProfile -File "$SCRIPT_DIR/../validate-publish-output.ps1" \
    -PublishDirectory "$APP_PATH/Contents/MacOS" \
    -RuntimeIdentifier "$EXPECTED_RUNTIME_IDENTIFIER" \
    -ExpectedVersion "$EXPECTED_VERSION" \
    -OutputPath "$COPY_ROOT/verified-publish-manifest.json" \
    -ExpectedManifestPath "$EXPECTED_MANIFEST_PATH"
fi

validate_app_boundary "$APP_PATH" "mounted DMG"

COPIED_APP_PATH="$COPY_ROOT/$(basename "$APP_PATH")"
/usr/bin/ditto "$APP_PATH" "$COPIED_APP_PATH"

validate_app_boundary "$COPIED_APP_PATH" "installed copy"
