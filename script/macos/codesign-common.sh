#!/bin/bash

resolve_signing_identity() {
  if [ "${MACOS_ADHOC_SIGNING:-false}" = "true" ]; then
    printf '%s\n' "-"
    return 0
  fi

  if [ -n "${MACOS_SIGNING_IDENTITY:-}" ]; then
    printf '%s\n' "$MACOS_SIGNING_IDENTITY"
    return 0
  fi

  if ! command -v security >/dev/null 2>&1; then
    echo "::error::security is required to resolve a Developer ID signing identity." >&2
    return 1
  fi

  local identity
  identity="$(security find-identity -v -p codesigning | awk -F '"' '/Developer ID Application/ { print $2; exit }')"
  if [ -z "$identity" ]; then
    echo "::error::No Developer ID Application signing identity is available." >&2
    return 1
  fi

  printf '%s\n' "$identity"
}
