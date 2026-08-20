#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/codesign-common.sh"

APP_NAME="${1:-哔哩下载姬.app}"
ENTITLEMENTS="${MACOS_ENTITLEMENTS:-$SCRIPT_DIR/DownKyi.entitlements}"
SIGNING_IDENTITY="$(resolve_signing_identity)"

codesign_app_path() {
  local path="$1"
  if [ "$SIGNING_IDENTITY" = "-" ]; then
    codesign --force --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$path"
  else
    codesign --force --timestamp --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$path"
  fi
}

find "$APP_NAME/Contents" -type f -print0 | while IFS= read -r -d '' file; do
  if is_signable_app_file "$file"; then
    echo "[INFO] Signing $file"
    codesign_app_path "$file"
  fi
done

echo "[INFO] Signing app bundle"

codesign_app_path "$APP_NAME"
