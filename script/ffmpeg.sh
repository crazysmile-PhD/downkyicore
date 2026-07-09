#!/bin/bash
set -euo pipefail

os=$1
arch=$2

ffmpeg_save_path="../DownKyi.Core/Binary"
download_dir="./downloads"
manifest="./assets/external-assets.json"

create_dir() {
  if [ ! -d "$1" ]; then
    mkdir -p "$1"
  fi
}

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{ print $1 }'
  else
    shasum -a 256 "$1" | awk '{ print $1 }'
  fi
}

verify_asset() {
  local file=$1
  local expected=$2
  local actual
  actual=$(sha256_file "$file")
  if [ "$actual" != "$expected" ]; then
    echo "Checksum mismatch for $file. Expected $expected, got $actual." >&2
    exit 1
  fi
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

  create_dir "$destination"
  find "$destination" -maxdepth 1 -type f -delete
  cp "$ffmpeg_bin" "$destination/ffmpeg"
  copy_license_files "$(dirname "$ffmpeg_bin")" "$destination"
  chmod +x "$destination/ffmpeg"
}

create_dir "$download_dir"

download_ffmpeg_macos() {
  local rid="osx-$arch"
  local url
  local expected_sha256
  url=$(asset_value "$rid" "url")
  expected_sha256=$(asset_value "$rid" "sha256")
  local archive="$download_dir/ffmpeg-mac-$arch.zip"
  curl -kL "$url" -o "$archive"
  verify_asset "$archive" "$expected_sha256"
  extract_ffmpeg "$archive" "$ffmpeg_save_path/$rid/ffmpeg"
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
  curl -kL "$url" -o "$archive"
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
