#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/codesign-common.sh"

FIXTURE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/downkyi-signing-coverage.XXXXXX")"
SELECTED_FILES="$FIXTURE_ROOT/selected-files.txt"
trap 'rm -rf "$FIXTURE_ROOT"' EXIT

mkdir -p "$FIXTURE_ROOT/Test.app/Contents/MacOS"
printf 'fixture\n' > "$FIXTURE_ROOT/Test.app/Contents/MacOS/DownKyi"
printf 'fixture\n' > "$FIXTURE_ROOT/Test.app/Contents/MacOS/libfixture.dylib"
printf 'fixture\n' > "$FIXTURE_ROOT/Test.app/Contents/MacOS/ManagedDependency.dll"
printf '{}\n' > "$FIXTURE_ROOT/Test.app/Contents/MacOS/runtimeconfig.json"

file() {
  case "$1" in
    */DownKyi|*.dylib)
      printf '%s: Mach-O 64-bit executable\n' "$1"
      ;;
    *.dll)
      printf '%s: PE32 executable Mono/.Net assembly\n' "$1"
      ;;
    *)
      printf '%s: ASCII text\n' "$1"
      ;;
  esac
}

: > "$SELECTED_FILES"
find "$FIXTURE_ROOT/Test.app/Contents/MacOS" -type f -print0 |
  while IFS= read -r -d '' path; do
    if is_signable_app_file "$path"; then
      basename "$path" >> "$SELECTED_FILES"
    fi
  done

grep -Fxq "DownKyi" "$SELECTED_FILES"
grep -Fxq "libfixture.dylib" "$SELECTED_FILES"
grep -Fxq "ManagedDependency.dll" "$SELECTED_FILES"
if grep -Fxq "runtimeconfig.json" "$SELECTED_FILES"; then
  echo "Non-code resources must not be selected for signing." >&2
  exit 1
fi

selected_count="$(wc -l < "$SELECTED_FILES")"
if [ "$selected_count" -ne 3 ]; then
  echo "Expected exactly three signable fixture files, found $selected_count." >&2
  exit 1
fi
