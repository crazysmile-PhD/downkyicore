#!/bin/bash
set -euo pipefail

APP_PATH="${1:?App bundle path is required.}"
RUNTIME_IDENTIFIER="${2:?Runtime identifier is required.}"

case "$RUNTIME_IDENTIFIER" in
  osx-x64) EXPECTED_ARCH="x86_64" ;;
  osx-arm64) EXPECTED_ARCH="arm64" ;;
  *)
    echo "::error::Unsupported v1.1.3 macOS runtime identifier: $RUNTIME_IDENTIFIER" >&2
    exit 1
    ;;
esac

for relative_path in DownKyi aria2/aria2c ffmpeg/ffmpeg ffmpeg/ffprobe; do
  binary="$APP_PATH/Contents/MacOS/$relative_path"
  if [ ! -f "$binary" ]; then
    echo "::error::Mounted app is missing runtime binary: $relative_path" >&2
    exit 1
  fi
  architectures="$(lipo -archs "$binary" 2>/dev/null || true)"
  if [ "$architectures" != "$EXPECTED_ARCH" ]; then
    echo "::error::Mounted runtime binary $relative_path has architecture '$architectures', expected '$EXPECTED_ARCH'." >&2
    exit 1
  fi
done
