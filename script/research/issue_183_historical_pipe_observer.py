#!/usr/bin/env python3
from __future__ import annotations

import argparse
import ctypes
import json
import os
import time
from pathlib import Path

import issue_183_desktop_assembly_info_probe as base

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
SYNCHRONIZE = 0x00100000
WAIT_TIMEOUT = 0x00000102


def process_is_alive(pid: int) -> bool:
    if os.name != "nt":
        try:
            os.kill(pid, 0)
            return True
        except OSError:
            return False
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, False, pid)
    if not handle:
        return False
    try:
        return kernel32.WaitForSingleObject(handle, 0) == WAIT_TIMEOUT
    finally:
        kernel32.CloseHandle(handle)


def read_control_text(path: Path) -> tuple[str | None, bool]:
    if not path.exists():
        return None, False
    try:
        return path.read_text(encoding="utf-8"), False
    except PermissionError:
        return None, True


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--control-dir", type=Path, required=True)
    parser.add_argument("--dump-tool", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=int, default=30)
    args = parser.parse_args()

    control = args.control_dir.resolve()
    control.mkdir(parents=True, exist_ok=True)
    pid_path = control / "target-pid.txt"
    exit_path = control / "target-exit.json"
    stdout_path = control / "stdout.txt"
    stderr_path = control / "stderr.txt"
    result_path = control / "observer-result.json"

    started = time.perf_counter()
    deadline = started + args.timeout_seconds
    target_pid: int | None = None
    pid_seen_at: float | None = None
    target_exit_code: int | None = None
    prearm_attempted = False
    alive_500ms = False
    prearm: dict | None = None
    timed_out = False
    observer_errors: list[str] = []
    control_contention_count = 0

    while True:
        try:
            if target_pid is None:
                value, contended = read_control_text(pid_path)
                if contended:
                    control_contention_count += 1
                elif value is not None and value.strip():
                    target_pid = int(value.strip())
                    pid_seen_at = time.perf_counter()

            if (
                target_pid is not None
                and pid_seen_at is not None
                and not prearm_attempted
                and time.perf_counter() - pid_seen_at >= 0.500
            ):
                prearm_attempted = True
                if process_is_alive(target_pid):
                    alive_500ms = True
                    prearm = base.capture_dump(
                        target_pid,
                        args.dump_tool.resolve(),
                        control / "prearm-500ms-after-start",
                        "alive-500ms-after-target-start-historical-pipe-drain",
                    )

            exit_text, contended = read_control_text(exit_path)
            if contended:
                control_contention_count += 1
            elif exit_text is not None:
                payload = json.loads(exit_text)
                target_exit_code = int(payload["exitCode"])
                break

            if time.perf_counter() >= deadline:
                timed_out = True
                if target_pid is not None and process_is_alive(target_pid):
                    if prearm is None or not prearm.get("dumpSucceeded", False):
                        prearm = base.capture_dump(
                            target_pid,
                            args.dump_tool.resolve(),
                            control / "timeout-evidence",
                            "phase-timeout-historical-pipe-drain",
                        )
                break

            time.sleep(0.002)
        except Exception as exc:
            observer_errors.append(repr(exc))
            break

    stdout = ""
    stderr = ""
    if target_exit_code is not None:
        try:
            stdout = stdout_path.read_text(encoding="utf-8", errors="replace")
            stderr = stderr_path.read_text(encoding="utf-8", errors="replace")
        except Exception as exc:
            observer_errors.append(f"final-output-read: {exc!r}")

    json_seen = any(base.is_assembly_info_json(line) for line in stdout.splitlines())
    watchdog = base.WATCHDOG in stdout or base.WATCHDOG in stderr
    confirmed = watchdog and bool(prearm and prearm.get("dumpSucceeded", False))
    valid = (
        not timed_out
        and target_pid is not None
        and target_exit_code == 0
        and json_seen
        and not observer_errors
    )

    result = {
        "targetProcessId": target_pid,
        "targetExitCode": target_exit_code,
        "assemblyInfoJsonSeen": json_seen,
        "executionValid": valid,
        "observerErrors": observer_errors,
        "controlContentionCount": control_contention_count,
        "alive500msAfterStart": alive_500ms,
        "watchdogSeen": watchdog,
        "timedOut": timed_out,
        "prearmCapture": prearm,
        "confirmedWatchdogDump": confirmed,
        "durationMs": round((time.perf_counter() - started) * 1000, 3),
    }
    base.write_json(result_path, result)
    print(json.dumps(result, indent=2))

    if not valid:
        return 4
    if watchdog and not confirmed:
        return 3
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
