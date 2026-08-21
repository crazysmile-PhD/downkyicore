#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/codesign-common.sh"

APP_NAME="${1:-哔哩下载姬.app}"
ENTITLEMENTS="${MACOS_ENTITLEMENTS:-$SCRIPT_DIR/DownKyi.entitlements}"
SIGNING_IDENTITY="$(resolve_signing_identity)"
MAIN_EXECUTABLE_NAME="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP_NAME/Contents/Info.plist")"
MAIN_EXECUTABLE="$APP_NAME/Contents/MacOS/$MAIN_EXECUTABLE_NAME"

if [ ! -f "$MAIN_EXECUTABLE" ]; then
  echo "::error::CFBundleExecutable does not exist: $MAIN_EXECUTABLE" >&2
  exit 1
fi

codesign_app_path() {
  local path="$1"
  if [ "$SIGNING_IDENTITY" = "-" ]; then
    codesign --force --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$path"
  else
    codesign --force --timestamp --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$path"
  fi
}

find "$APP_NAME/Contents" -type f -print0 | while IFS= read -r -d '' file; do
  if [ "$file" = "$MAIN_EXECUTABLE" ]; then
    continue
  fi

  if is_signable_app_file "$file"; then
    echo "[INFO] Signing $file"
    codesign_app_path "$file"
  fi
done

echo "[INFO] Signing main executable $MAIN_EXECUTABLE"
codesign_app_path "$MAIN_EXECUTABLE"

echo "[INFO] Signing app bundle"

codesign_app_path "$APP_NAME"
