#!/bin/bash
set -euo pipefail

MODE="${1:?Mode is required: verify or refresh.}"
APP_PATH="${2:?App bundle path is required.}"
RUNTIME_IDENTIFIER="${3:-}"
ARIA_EXECUTABLE="$APP_PATH/Contents/MacOS/aria2/aria2c"
CHECKSUM_LINK="$ARIA_EXECUTABLE.sha256"
CHECKSUM_TARGET="$APP_PATH/Contents/Resources/dotnet/aria2/aria2c.sha256"
EXPECTED_LINK_TARGET="../../Resources/dotnet/aria2/aria2c.sha256"

fail() {
  echo "::error::aria2 runtime integrity invariant failed: $*" >&2
  exit 1
}

sha256_file() {
  shasum -a 256 "$1" | awk '{ print tolower($1) }'
}

if [ "$MODE" != "verify" ] && [ "$MODE" != "refresh" ]; then
  fail "unsupported mode '$MODE'; expected verify or refresh."
fi

if [ ! -d "$APP_PATH/Contents" ]; then
  fail "app bundle Contents directory is missing: $APP_PATH/Contents"
fi

if [ ! -f "$ARIA_EXECUTABLE" ] || [ -L "$ARIA_EXECUTABLE" ]; then
  fail "aria2 executable is missing or is not a regular bundled file: $ARIA_EXECUTABLE"
fi

if [ ! -x "$ARIA_EXECUTABLE" ]; then
  fail "aria2 executable permission is missing: $ARIA_EXECUTABLE"
fi

if [ ! -L "$CHECKSUM_LINK" ]; then
  fail "runtime checksum path must remain a symlink: $CHECKSUM_LINK"
fi

ACTUAL_LINK_TARGET="$(readlink "$CHECKSUM_LINK")"
if [ "$ACTUAL_LINK_TARGET" != "$EXPECTED_LINK_TARGET" ]; then
  fail "runtime checksum symlink target is '$ACTUAL_LINK_TARGET', expected '$EXPECTED_LINK_TARGET'."
fi

if [ ! -f "$CHECKSUM_TARGET" ] || [ -L "$CHECKSUM_TARGET" ]; then
  fail "runtime checksum target is missing or is not a regular file: $CHECKSUM_TARGET"
fi

if [ ! "$CHECKSUM_LINK" -ef "$CHECKSUM_TARGET" ]; then
  fail "runtime checksum symlink does not resolve to the canonical Resources target."
fi

if [ "$MODE" = "refresh" ]; then
  if codesign -d "$APP_PATH" >/dev/null 2>&1; then
    fail "refusing to modify the runtime checksum after the outer app signature is sealed."
  fi

  FINAL_SHA256="$(sha256_file "$ARIA_EXECUTABLE")"
  printf '%s' "$FINAL_SHA256" >"$CHECKSUM_LINK"
  echo "[INFO] Refreshed runtime aria2 checksum after nested signing."
fi

CHECKSUM_TEXT="$(<"$CHECKSUM_LINK")"
if [[ ! "$CHECKSUM_TEXT" =~ ^[[:xdigit:]]{64}$ ]]; then
  fail "runtime checksum sidecar must contain exactly one 64-character SHA-256 digest."
fi

EXPECTED_SHA256="$(printf '%s' "$CHECKSUM_TEXT" | tr '[:upper:]' '[:lower:]')"
ACTUAL_SHA256="$(sha256_file "$ARIA_EXECUTABLE")"
if [ "$ACTUAL_SHA256" != "$EXPECTED_SHA256" ]; then
  fail "final aria2 executable SHA-256 '$ACTUAL_SHA256' does not match runtime sidecar '$EXPECTED_SHA256'."
fi

if [ -n "$RUNTIME_IDENTIFIER" ]; then
  case "$RUNTIME_IDENTIFIER" in
    osx-x64) EXPECTED_ARCHITECTURE="x86_64" ;;
    osx-arm64) EXPECTED_ARCHITECTURE="arm64" ;;
    *) fail "unsupported macOS runtime identifier: $RUNTIME_IDENTIFIER" ;;
  esac

  ACTUAL_ARCHITECTURE="$(lipo -archs "$ARIA_EXECUTABLE" 2>/dev/null || true)"
  if [ "$ACTUAL_ARCHITECTURE" != "$EXPECTED_ARCHITECTURE" ]; then
    fail "aria2 executable architecture is '$ACTUAL_ARCHITECTURE', expected '$EXPECTED_ARCHITECTURE' for $RUNTIME_IDENTIFIER."
  fi
fi

echo "[INFO] Final aria2 runtime integrity verified${RUNTIME_IDENTIFIER:+ for $RUNTIME_IDENTIFIER}."
