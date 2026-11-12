#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo 'Usage: ffmpeg.sh {linux|mac} {x64|arm64}' >&2
  exit 2
fi

case "$1" in
  linux) platform=linux ;;
  mac) platform=osx ;;
  *) echo "Unsupported FFmpeg platform: $1" >&2; exit 2 ;;
esac

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
python3 "$script_dir/ffmpeg-source/build.py" "$platform-$2"
