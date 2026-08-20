#!/bin/bash
set -euo pipefail

DMG_PATH="${1:?DMG path is required.}"

codesign --verify --verbose=2 "$DMG_PATH"

if [ "${MACOS_VERIFY_NOTARIZATION:-false}" = "true" ]; then
  xcrun stapler validate "$DMG_PATH"
  spctl --assess --type open --context context:primary-signature --verbose=4 "$DMG_PATH"
fi
