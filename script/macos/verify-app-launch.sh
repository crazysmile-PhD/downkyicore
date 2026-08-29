#!/bin/bash
set -euo pipefail

APP_NAME="${1:-哔哩下载姬.app}"
EXECUTABLE_NAME="${MACOS_EXECUTABLE_NAME:-DownKyi}"
LAUNCH_SECONDS="${MACOS_LAUNCH_SECONDS:-8}"
EXECUTABLE="$APP_NAME/Contents/MacOS/$EXECUTABLE_NAME"
LOG_FILE="$(mktemp "${TMPDIR:-/tmp}/downkyi-app-launch.XXXXXX")"
PID=""

process_group_is_alive() {
  kill -0 -- "-$PID" 2>/dev/null
}

terminate_process_group() {
  local attempts=0

  kill -TERM -- "-$PID" 2>/dev/null || true
  while process_group_is_alive && [ "$attempts" -lt 20 ]; do
    sleep 0.25
    attempts=$((attempts + 1))
  done

  if process_group_is_alive; then
    kill -KILL -- "-$PID" 2>/dev/null || true
  fi

  wait "$PID" 2>/dev/null || true
}

cleanup() {
  if [ -n "$PID" ] && process_group_is_alive; then
    terminate_process_group
  fi
  rm -f "$LOG_FILE"
}
trap cleanup EXIT

if [ ! -x "$EXECUTABLE" ]; then
  echo "::error::Packaged app executable is missing or not executable: $EXECUTABLE" >&2
  exit 1
fi

set -m
"$EXECUTABLE" >"$LOG_FILE" 2>&1 &
PID=$!
set +m
sleep "$LAUNCH_SECONDS"

if ! kill -0 "$PID" 2>/dev/null; then
  wait "$PID" || status=$?
  echo "::error::Packaged app exited before the launch verification window (status ${status:-0})." >&2
  cat "$LOG_FILE" >&2
  exit 1
fi

echo "[INFO] Packaged app remained running for $LAUNCH_SECONDS seconds."
