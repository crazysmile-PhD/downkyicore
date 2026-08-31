#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${CANONICAL_PUBLISH_DIRECTORY:-}" ]]; then
  echo 'CANONICAL_PUBLISH_DIRECTORY is required for standalone PupNet packaging.' >&2
  exit 1
fi
if [[ -z "${BUILD_APP_BIN:-}" ]]; then
  echo 'BUILD_APP_BIN was not supplied by PupNet.' >&2
  exit 1
fi
if [[ ! -d "$CANONICAL_PUBLISH_DIRECTORY" ]]; then
  echo 'Canonical publish directory does not exist.' >&2
  exit 1
fi
if [[ ! -d "$BUILD_APP_BIN" ]]; then
  echo 'PupNet BUILD_APP_BIN does not exist.' >&2
  exit 1
fi
if [[ "$(realpath "$CANONICAL_PUBLISH_DIRECTORY")" == "$(realpath "$BUILD_APP_BIN")" ]]; then
  echo 'Canonical publish source and PupNet staging destination must be distinct.' >&2
  exit 1
fi
if [[ -z "$(find "$CANONICAL_PUBLISH_DIRECTORY" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo 'Canonical publish payload is empty.' >&2
  exit 1
fi
if [[ -n "$(find "$BUILD_APP_BIN" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo 'PupNet staging destination must be empty before the canonical payload is copied.' >&2
  exit 1
fi

cp -a -- "$CANONICAL_PUBLISH_DIRECTORY"/. "$BUILD_APP_BIN"/
echo 'Staged canonical publish payload for PupNet packaging.'
