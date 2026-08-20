#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/codesign-common.sh"

APP_NAME="${1:-哔哩下载姬.app}"
ENTITLEMENTS="${MACOS_ENTITLEMENTS:-$SCRIPT_DIR/DownKyi.entitlements}"
SIGNING_IDENTITY="$(resolve_signing_identity)"
set_codesign_timestamp_args "$SIGNING_IDENTITY"

find "$APP_NAME/Contents" -type f -print0 | while IFS= read -r -d '' file; do
  if file "$file" | grep -q "Mach-O"; then
    echo "[INFO] Signing $file"
    codesign --force "${CODESIGN_TIMESTAMP_ARGS[@]}" --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$file"
  fi
done

echo "[INFO] Signing app bundle"

codesign --force "${CODESIGN_TIMESTAMP_ARGS[@]}" --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$APP_NAME"
