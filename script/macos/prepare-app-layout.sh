#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/codesign-common.sh"

APP_NAME="${1:-哔哩下载姬.app}"
CODE_DIRECTORY="$APP_NAME/Contents/MacOS"
RESOURCE_DIRECTORY="$APP_NAME/Contents/Resources/dotnet"

if [ ! -d "$CODE_DIRECTORY" ]; then
  echo "::error::macOS code directory does not exist: $CODE_DIRECTORY" >&2
  exit 1
fi

relative_resource_link() {
  local relative_path="$1"
  local parent_path
  local prefix="../"

  parent_path="$(dirname "$relative_path")"
  while [ "$parent_path" != "." ]; do
    prefix="../$prefix"
    parent_path="$(dirname "$parent_path")"
  done

  printf '%s\n' "${prefix}Resources/dotnet/$relative_path"
}

find "$CODE_DIRECTORY" -type f -print0 |
  while IFS= read -r -d '' path; do
    if is_signable_app_file "$path"; then
      continue
    fi

    relative_path="${path#"$CODE_DIRECTORY"/}"
    destination="$RESOURCE_DIRECTORY/$relative_path"
    mkdir -p "$(dirname "$destination")"
    mv "$path" "$destination"
    ln -s "$(relative_resource_link "$relative_path")" "$path"
    echo "[INFO] Moved non-code bundle content to Resources: $relative_path"
  done

find "$CODE_DIRECTORY" -type f -print0 |
  while IFS= read -r -d '' path; do
    if ! is_signable_app_file "$path"; then
      echo "::error::Unsigned data remains in the macOS code directory: $path" >&2
      exit 1
    fi
  done
