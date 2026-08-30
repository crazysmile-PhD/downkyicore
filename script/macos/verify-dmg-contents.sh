#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DMG_PATH="${1:?DMG path is required.}"
EXPECTED_VERSION="${2:?Expected release version is required.}"
EXPECTED_RUNTIME_IDENTIFIER="${3:?Expected runtime identifier is required.}"
MOUNT_POINT="$(mktemp -d "${TMPDIR:-/tmp}/downkyi-dmg.XXXXXX")"
ATTACHED=false

cleanup() {
  if [ "$ATTACHED" = "true" ]; then
    hdiutil detach "$MOUNT_POINT" -quiet || hdiutil detach "$MOUNT_POINT" -force -quiet || true
  fi
  rmdir "$MOUNT_POINT" 2>/dev/null || true
}
trap cleanup EXIT

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

"$SCRIPT_DIR/verify-runtime-architecture.sh" "$APP_PATH" "$EXPECTED_RUNTIME_IDENTIFIER"

"$SCRIPT_DIR/verify-app.sh" "$APP_PATH"
"$SCRIPT_DIR/verify-app-launch.sh" "$APP_PATH"
