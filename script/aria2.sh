#!/bin/bash
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/.." && pwd)
download_dir="$script_dir/downloads"
save_path="$repo_root/DownKyi.Core/Binary"
manifest="$script_dir/assets/external-assets.json"

if [ ! -d "$download_dir" ]; then
  mkdir "$download_dir"
fi

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

asset_value() {
  local rid=$1
  local key=$2
  python3 - "$manifest" "$rid" "$key" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as f:
    manifest = json.load(f)

print(manifest["aria2"]["assets"][sys.argv[2]][sys.argv[3]])
PY
}

download_aria2() {
  local rid=$1
  local download_url
  local expected_sha256
  local save="$save_path/$rid/aria2"
  download_url=$(asset_value "$rid" "url")
  expected_sha256=$(asset_value "$rid" "sha256")

  local archive="$download_dir/aria2-$rid.zip"
  curl --fail --location --show-error "$download_url" -o "$archive"
  verify_asset "$archive" "$expected_sha256"
  create_dir "$save"
  unzip -o -d "$save" "$archive"
  chmod +x "$save/aria2c"
}

download_aria2 "$@"
