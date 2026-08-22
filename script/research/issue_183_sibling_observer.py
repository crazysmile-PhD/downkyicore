#!/usr/bin/env python3
from __future__ import annotations

import argparse
import ctypes
import json
import os
import subprocess
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
    handle = kernel32.OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
        False,
        pid,
    )
    if not handle:
        return False
    try:
        return kernel32.WaitForSingleObject(handle, 0) == WAIT_TIMEOUT
    finally:
        kernel32.CloseHandle(handle)


def read_new_lines(path: Path, offset: int) -> tuple[list[str], int]:
    if not path.exists():
        return [], offset
    with path.open("r", encoding="utf-8", errors="replace", newline="") as stream:
        stream.seek(offset)
        text = stream.read()
        new_offset = stream.tell()
    if not text:
        return [], new_offset
    return text.splitlines(), new_offset


def kill_target_tree(pid: int, log: Path) -> None:
    base.run_tool(["taskkill", "/PID", str(pid), "/T", "/F"], log, timeout=15)


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
    target_exit_code: int | None = None
    stdout_offset = 0
    stderr_offset = 0
    json_seen_at: float | None = None
    json_seen_utc: str | None = None
    watchdog = False
    watchdog_utc: str | None = None
    prearm_attempted = False
    alive_500ms = False
    prearm: dict | None = None
    exact: dict | None = None
    timed_out = False
    observer_errors: list[str] = []

    while True:
        try:
            if target_pid is None and pid_path.exists():
                value = pid_path.read_text(encoding="utf-8").strip()
                if value:
                    target_pid = int(value)

            for path, stream_name in ((stdout_path, "stdout"), (stderr_path, "stderr")):
                offset = stdout_offset if stream_name == "stdout" else stderr_offset
                lines, new_offset = read_new_lines(path, offset)
                if stream_name == "stdout":
                    stdout_offset = new_offset
                else:
                    stderr_offset = new_offset
                for line in lines:
                    now_perf = time.perf_counter()
                    now_utc = base.now()
                    if stream_name == "stdout" and json_seen_at is None and base.is_assembly_info_json(line):
                        json_seen_at = now_perf
                        json_seen_utc = now_utc
                    if base.WATCHDOG in line and not watchdog:
                        watchdog = True
                        watchdog_utc = now_utc
                        if target_pid is not None and (prearm is None or not prearm.get("dumpSucceeded", False)):
                            exact = base.capture_dump(
                                target_pid,
                                args.dump_tool.resolve(),
                                control / "exact-watchdog",
                                "exact-watchdog-sibling-observer",
                            )

            if (
                json_seen_at is not None
                and not prearm_attempted
                and time.perf_counter() - json_seen_at >= 0.500
            ):
                prearm_attempted = True
                if target_pid is not None and process_is_alive(target_pid):
                    alive_500ms = True
                    prearm = base.capture_dump(
                        target_pid,
                        args.dump_tool.resolve(),
                        control / "prearm-500ms",
                        "alive-500ms-after-json-sibling-observer",
                    )

            if exit_path.exists():
                payload = json.loads(exit_path.read_text(encoding="utf-8"))
                target_exit_code = int(payload["exitCode"])
                # One final scan after the launcher has drained both redirected streams.
                for path, stream_name in ((stdout_path, "stdout"), (stderr_path, "stderr")):
                    offset = stdout_offset if stream_name == "stdout" else stderr_offset
                    lines, new_offset = read_new_lines(path, offset)
                    if stream_name == "stdout":
                        stdout_offset = new_offset
                    else:
                        stderr_offset = new_offset
                    for line in lines:
                        now_perf = time.perf_counter()
                        now_utc = base.now()
                        if stream_name == "stdout" and json_seen_at is None and base.is_assembly_info_json(line):
                            json_seen_at = now_perf
                            json_seen_utc = now_utc
                        if base.WATCHDOG in line and not watchdog:
                            watchdog = True
                            watchdog_utc = now_utc
                            if target_pid is not None and (prearm is None or not prearm.get("dumpSucceeded", False)):
                                exact = base.capture_dump(
                                    target_pid,
                                    args.dump_tool.resolve(),
                                    control / "exact-watchdog",
                                    "exact-watchdog-sibling-observer-final-scan",
                                )
                break

            if time.perf_counter() >= deadline:
                timed_out = True
                if target_pid is not None and process_is_alive(target_pid):
                    if prearm is None or not prearm.get("dumpSucceeded", False):
                        prearm = base.capture_dump(
                            target_pid,
                            args.dump_tool.resolve(),
                            control / "timeout-evidence",
                            "phase-timeout-sibling-observer",
                        )
                    kill_target_tree(target_pid, control / "taskkill.log")
                break

            time.sleep(0.002)
        except Exception as exc:
            observer_errors.append(repr(exc))
            break

    confirmed = watchdog and bool(
        (prearm and prearm.get("dumpSucceeded", False))
        or (exact and exact.get("dumpSucceeded", False))
    )
    valid = (
        not timed_out
        and target_pid is not None
        and target_exit_code == 0
        and json_seen_utc is not None
        and not observer_errors
    )
    result = {
        "targetProcessId": target_pid,
        "targetExitCode": target_exit_code,
        "assemblyInfoJsonSeenAtUtc": json_seen_utc,
        "executionValid": valid,
        "observerErrors": observer_errors,
        "alive500msAfterJson": alive_500ms,
        "watchdogSeen": watchdog,
        "watchdogSeenAtUtc": watchdog_utc,
        "timedOut": timed_out,
        "prearmCapture": prearm,
        "exactCapture": exact,
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
