#!/bin/bash
set -euo pipefail

os=$1
arch=$2

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/.." && pwd)
ffmpeg_save_path="$repo_root/DownKyi.Core/Binary"
download_dir="$script_dir/downloads"
manifest="$script_dir/assets/external-assets.json"

create_dir() {
  if [ ! -d "$1" ]; then
    mkdir -p "$1"
  fi
}

verify_asset() {
  local file=$1
  local expected=$2
  python3 "$script_dir/ffmpeg-assets.py" verify-file --path "$file" --sha256 "$expected"
}

copy_license_files() {
  local source_dir=$1
  local destination=$2

  while [[ "$source_dir" == "$extract_dir"* ]]; do
    find "$source_dir" -maxdepth 1 -type f \( \
      -iname 'LICENSE' -o -iname 'LICENSE.*' -o \
      -iname 'COPYING' -o -iname 'COPYING.*' -o \
      -iname 'README' -o -iname 'README.*' \
    \) -exec cp {} "$destination/" \;
    source_dir=$(dirname "$source_dir")
  done
}

asset_value() {
  local rid=$1
  local key=$2
  python3 - "$manifest" "$rid" "$key" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as f:
    manifest = json.load(f)

print(manifest["ffmpeg"]["assets"][sys.argv[2]][sys.argv[3]])
PY
}

extract_ffmpeg() {
  local archive=$1
  local destination=$2
  local ffprobe_archive=${3:-}
  local extract_dir="$download_dir/ffmpeg-extract-$os-$arch"

  rm -rf "$extract_dir"
  create_dir "$extract_dir"
  case "$archive" in
  *.tar.xz)
    tar -xJf "$archive" -C "$extract_dir"
    ;;
  *.zip)
    unzip -q -d "$extract_dir" -o "$archive"
    ;;
  *)
    echo "Unsupported ffmpeg archive: $archive" >&2
    exit 1
    ;;
  esac

  local ffmpeg_bin
  ffmpeg_bin=$(find "$extract_dir" -type f -name ffmpeg | head -n 1)
  if [ -z "$ffmpeg_bin" ]; then
    echo "ffmpeg binary not found in $archive" >&2
    exit 1
  fi

  local ffprobe_bin
  ffprobe_bin=$(find "$extract_dir" -type f -name ffprobe | head -n 1)
  if [ -z "$ffprobe_bin" ] && [ -n "$ffprobe_archive" ]; then
    local probe_extract_dir="$extract_dir/ffprobe"
    create_dir "$probe_extract_dir"
    unzip -q -d "$probe_extract_dir" -o "$ffprobe_archive"
    ffprobe_bin=$(find "$probe_extract_dir" -type f -name ffprobe | head -n 1)
  fi
  if [ -z "$ffprobe_bin" ]; then
    echo "ffprobe binary not found in supplied archives" >&2
    exit 1
  fi

  create_dir "$destination"
  find "$destination" -maxdepth 1 -type f -delete
  cp "$ffmpeg_bin" "$destination/ffmpeg"
  cp "$ffprobe_bin" "$destination/ffprobe"
  copy_license_files "$(dirname "$ffmpeg_bin")" "$destination"
  chmod +x "$destination/ffmpeg" "$destination/ffprobe"
}

create_dir "$download_dir"

download_ffmpeg_macos() {
  local rid="osx-$arch"
  local url
  local expected_sha256
  url=$(asset_value "$rid" "url")
  expected_sha256=$(asset_value "$rid" "sha256")
  local archive="$download_dir/ffmpeg-mac-$arch.zip"
  local ffprobe_url
  local ffprobe_sha256
  ffprobe_url=$(asset_value "$rid" "ffprobeUrl")
  ffprobe_sha256=$(asset_value "$rid" "ffprobeSha256")
  local ffprobe_archive="$download_dir/ffprobe-mac-$arch.zip"
  curl --fail --location --show-error "$url" -o "$archive"
  verify_asset "$archive" "$expected_sha256"
  curl --fail --location --show-error "$ffprobe_url" -o "$ffprobe_archive"
  verify_asset "$ffprobe_archive" "$ffprobe_sha256"
  extract_ffmpeg "$archive" "$ffmpeg_save_path/$rid/ffmpeg" "$ffprobe_archive"
}

download_ffmpeg_linux() {
  local rid="linux-$arch"
  local url
  local expected_sha256
  url=$(asset_value "$rid" "url")
  expected_sha256=$(asset_value "$rid" "sha256")
  local archive="$download_dir/ffmpeg-linux-$arch.${url##*.}"
  if [[ "$url" == *.tar.xz ]]; then
    archive="$download_dir/ffmpeg-linux-$arch.tar.xz"
  fi
  curl --fail --location --show-error "$url" -o "$archive"
  verify_asset "$archive" "$expected_sha256"
  extract_ffmpeg "$archive" "$ffmpeg_save_path/$rid/ffmpeg"
}

if [ "$os" == "mac" ]; then
  download_ffmpeg_macos
elif [ "$os" == "linux" ]; then
  download_ffmpeg_linux
else
  echo "Unsupported operating system: $os" >&2
  exit 1
fi
