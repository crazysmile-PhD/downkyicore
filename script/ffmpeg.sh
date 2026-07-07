#!/bin/bash
set -euo pipefail

os=$1
arch=$2

ffmpeg_save_path="../DownKyi.Core/Binary"
download_dir="./downloads"

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

create_dir "$download_dir"

download_ffmpeg_macos() {
  local filename=""
  local expected_sha256=""
  case $arch in
  x64)
    filename=ffmpeg-x86_64-apple-darwin_static.zip
    expected_sha256="ba7cd3da928d9028b01cecc0fca915021afea47e9090552579d8da2919e7bde4"
    ;;
  arm64)
    filename=ffmpeg-aarch64-apple-darwin_static.zip
    expected_sha256="8fd7ea3126839dac99d8e631071ce8b08a1caa00d72171813b4e77aa1f68bb31"
    ;;
  *)
    echo "Unsupported macOS ffmpeg architecture: $arch" >&2
    exit 1
    ;;
  esac
  local url="https://github.com/yaobiao131/downkyi-ffmpeg-build/releases/download/continuous/$filename"
  local archive="$download_dir/ffmpeg-mac-$arch.zip"
  create_dir "$ffmpeg_save_path/osx-$arch/ffmpeg"
  curl -kL "$url" -o "$archive"
  verify_asset "$archive" "$expected_sha256"
  unzip -d "$ffmpeg_save_path/osx-$arch/ffmpeg/" -o "$archive"
  chmod +x "$ffmpeg_save_path/osx-$arch/ffmpeg/ffmpeg"
}

download_ffmpeg_linux() {
  local filename=""
  local expected_sha256=""
  case $arch in
  x64)
    filename=ffmpeg-x86_64-linux-musl_static.zip
    expected_sha256="fd6709e6c39aa6cdeb8f98b51c434122a4ae7dc36d5b94765c695e96c64647f2"
    ;;
  arm64)
    filename=ffmpeg-aarch64-linux-musl_static.zip
    expected_sha256="89b5d5f1dc7832dd07a4f171a236c089b55b087062232b6845c8ae31c4f16e23"
    ;;
  *)
    echo "Unsupported Linux ffmpeg architecture: $arch" >&2
    exit 1
    ;;
  esac
  local url="https://github.com/yaobiao131/downkyi-ffmpeg-build/releases/download/continuous/$filename"
  local archive="$download_dir/ffmpeg-linux-$arch.zip"
  create_dir "$ffmpeg_save_path/linux-$arch/ffmpeg"
  curl -kL "$url" -o "$archive"
  verify_asset "$archive" "$expected_sha256"
  unzip -d "$ffmpeg_save_path/linux-$arch/ffmpeg/" -o "$archive"
  chmod +x "$ffmpeg_save_path/linux-$arch/ffmpeg/ffmpeg"
}

if [ "$os" == "mac" ]; then
  download_ffmpeg_macos
elif [ "$os" == "linux" ]; then
  download_ffmpeg_linux
else
  echo "Unsupported operating system: $os" >&2
  exit 1
fi
