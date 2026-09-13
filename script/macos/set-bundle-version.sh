#!/bin/bash
set -euo pipefail

PLIST_PATH="${1:?Info.plist path is required.}"
VERSION="${2:?Release version is required.}"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "::error::Invalid release version for macOS bundle metadata: $VERSION" >&2
  exit 1
fi

/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$PLIST_PATH"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$PLIST_PATH"

if [ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$PLIST_PATH")" != "$VERSION" ] || \
   [ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$PLIST_PATH")" != "$VERSION" ]; then
  echo "::error::Failed to materialize macOS bundle version $VERSION." >&2
  exit 1
fi
