#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/codesign-common.sh"

DMG_PATH="${1:?DMG path is required.}"
SIGNING_IDENTITY="$(resolve_signing_identity)"

if [ "$SIGNING_IDENTITY" = "-" ]; then
  echo "::error::DMG signing requires a Developer ID identity." >&2
  exit 1
fi

codesign --force --timestamp --sign "$SIGNING_IDENTITY" "$DMG_PATH"
