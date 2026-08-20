#!/bin/bash
set -euo pipefail

APP_NAME="${1:-哔哩下载姬.app}"

codesign --verify --deep --strict --verbose=2 "$APP_NAME"
codesign -dv --verbose=4 "$APP_NAME"

if [ "${MACOS_VERIFY_GATEKEEPER:-false}" = "true" ]; then
  spctl --assess --type execute --verbose=4 "$APP_NAME"
fi
