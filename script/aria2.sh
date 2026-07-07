#!/bin/bash
set -euo pipefail

download_dir="./downloads"
save_path="../DownKyi.Core/Binary"

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

download_aria2() {
  local download_url=""
  local expected_sha256=""
  local save=""
  case $1 in
  linux-x64)
    save="$save_path/$1/aria2"
    download_url="https://github.com/yaobiao131/downkyi-aria2-static-build/releases/download/1.37.0/aria2-x86_64-linux-musl_static.zip"
    expected_sha256="37d9679f42b7d45b203b6497957c33693d07de3fb01f8009e55d83810f6a5faf"
    ;;
  linux-arm64)
    save="$save_path/$1/aria2"
    download_url="https://github.com/yaobiao131/downkyi-aria2-static-build/releases/download/1.37.0/aria2-aarch64-linux-musl_static.zip"
    expected_sha256="aa0fd7aefb43125c9ea558ada538d1171f2b56866b2acb8d01b2915060f60d37"
    ;;
  osx-x64)
    save="$save_path/$1/aria2"
    download_url="https://github.com/yaobiao131/downkyi-aria2-static-build/releases/download/1.37.0/aria2-x86_64-apple-darwin_static.zip"
    expected_sha256="95bd7654ae68c9049893c64e83f22a472d1ad9ff8b6aebe1b047106fa5c6bfc2"
    ;;
  osx-arm64)
    save="$save_path/$1/aria2"
    download_url="https://github.com/yaobiao131/downkyi-aria2-static-build/releases/download/1.37.0/aria2-aarch64-apple-darwin_static.zip"
    expected_sha256="5431ba2e1b81318e07646d53373d2da8348f3fdea3331cef98f02764c105bfe1"
    ;;
  *)
    echo "Unsupported aria2 runtime: $1" >&2
    exit 1
    ;;
  esac

  local archive="$download_dir/aria2-$1.zip"
  curl -kL "$download_url" -o "$archive"
  verify_asset "$archive" "$expected_sha256"
  create_dir "$save"
  unzip -o -d "$save" "$archive"
  chmod +x "$save/aria2c"
}

download_aria2 "$@"
