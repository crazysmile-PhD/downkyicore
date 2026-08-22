#!/usr/bin/env python3
from __future__ import annotations

import argparse
import ctypes
import json
import os
import queue
import subprocess
import threading
import time
from pathlib import Path

import issue_183_desktop_assembly_info_probe as base

CONTROL_PREFIX = "__ISSUE183_CONTROL__:"
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
SYNCHRONIZE = 0x00100000
WAIT_TIMEOUT = 0x00000102

SAFE_AMBIENT_NAMES = {
    "CI",
    "GITHUB_ACTIONS",
    "RUNNER_ARCH",
    "RUNNER_ENVIRONMENT",
    "RUNNER_OS",
    "ImageOS",
    "ImageVersion",
}
SAFE_AMBIENT_PREFIXES = (
    "COMPLUS_",
    "CORECLR_",
    "COREHOST_",
    "DOTNET_",
)
FORBIDDEN_ENV_TOKENS = (
    "AUTH",
    "BEARER",
    "COOKIE",
    "CREDENTIAL",
    "KEY",
    "PASSWORD",
    "SECRET",
    "TOKEN",
)


def historical_safe_child_env() -> dict[str, str]:
    """Restore only non-secret runtime/CI ambient state from the historical job.

    The original PowerShell harness inherited the Actions job environment. Full
    inheritance is unsafe because a same-PID Full dump can retain CI credentials.
    This controlled slice restores only runtime-affecting DOTNET/CLR/host variables
    and non-secret CI identity flags while rejecting credential-like names.
    """
    env = base.sanitized_child_env()
    for key, value in os.environ.items():
        upper = key.upper()
        selected = key in SAFE_AMBIENT_NAMES or any(
            upper.startswith(prefix) for prefix in SAFE_AMBIENT_PREFIXES
        )
        if not selected:
            continue
        if any(token in upper for token in FORBIDDEN_ENV_TOKENS):
            continue
        env[key] = value

    forbidden = sorted(
        key for key in env
        if any(token in key.upper() for token in FORBIDDEN_ENV_TOKENS)
    )
    if forbidden:
        raise RuntimeError(f"unsafe child environment keys selected: {forbidden}")
    return env


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


def kill_target_tree(pid: int, log: Path) -> int:
    return base.run_tool(["taskkill", "/PID", str(pid), "/T", "/F"], log, timeout=15)


def relay_reader(name: str, stream, path: Path, events: queue.Queue) -> None:
    error: str | None = None
    try:
        with path.open("w", encoding="utf-8", newline="") as f:
            for line in iter(stream.readline, ""):
                f.write(line)
                f.flush()
                events.put((name, line.rstrip("\r\n"), time.perf_counter(), base.now(), None))
    except Exception as exc:
        error = repr(exc)
    finally:
        events.put((name, None, time.perf_counter(), base.now(), error))


def run_once(
    dll: Path,
    cwd: Path,
    launcher: Path,
    dump_tool: Path,
    out: Path,
    iteration: int,
    timeout_seconds: int,
) -> dict:
    out.mkdir(parents=True, exist_ok=True)
    child_env = historical_safe_child_env()
    wrapper = subprocess.Popen(
        [
            "pwsh",
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-File",
            str(launcher),
            "-Repository",
            str(cwd),
            "-AssemblyPath",
            str(dll),
        ],
        cwd=str(cwd),
        env=child_env,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    assert wrapper.stdout is not None and wrapper.stderr is not None

    events: queue.Queue = queue.Queue()
    threads = [
        threading.Thread(
            target=relay_reader,
            args=("stdout", wrapper.stdout, out / "stdout.txt", events),
            daemon=True,
        ),
        threading.Thread(
            target=relay_reader,
            args=("stderr", wrapper.stderr, out / "stderr.txt", events),
            daemon=True,
        ),
    ]
    for thread in threads:
        thread.start()

    started = time.perf_counter()
    deadline = started + timeout_seconds
    target_pid: int | None = None
    target_exit_code: int | None = None
    json_seen_at: float | None = None
    json_seen_utc: str | None = None
    prearm_done = False
    watchdog = False
    watchdog_utc: str | None = None
    prearm: dict | None = None
    exact: dict | None = None
    timed_out = False
    reader_errors: list[dict[str, str]] = []
    done_streams: set[str] = set()

    while wrapper.poll() is None or len(done_streams) < 2 or not events.empty():
        try:
            stream, line, perf, utc, reader_error = events.get(timeout=0.01)
            if line is None:
                done_streams.add(stream)
                if reader_error is not None:
                    reader_errors.append({"stream": stream, "error": reader_error})
                    if wrapper.poll() is None:
                        wrapper.kill()
            else:
                if stream == "stderr" and line.startswith(CONTROL_PREFIX):
                    payload = line[len(CONTROL_PREFIX):]
                    if payload.startswith("PID="):
                        target_pid = int(payload[4:])
                    elif payload.startswith("EXIT="):
                        target_exit_code = int(payload[5:])
                    continue

                if stream == "stdout" and json_seen_at is None and base.is_assembly_info_json(line):
                    json_seen_at = perf
                    json_seen_utc = utc

                if base.WATCHDOG in line and not watchdog:
                    watchdog = True
                    watchdog_utc = utc
                    if target_pid is not None and (prearm is None or not prearm.get("dumpSucceeded", False)):
                        exact = base.capture_dump(
                            target_pid,
                            dump_tool,
                            out / "exact-watchdog",
                            "exact-watchdog-powershell-topology-safe-ambient",
                        )
        except queue.Empty:
            pass

        if (
            json_seen_at is not None
            and not prearm_done
            and time.perf_counter() - json_seen_at >= 0.500
        ):
            prearm_done = True
            if target_pid is not None and process_is_alive(target_pid):
                prearm = base.capture_dump(
                    target_pid,
                    dump_tool,
                    out / "prearm-500ms",
                    "alive-500ms-after-json-powershell-topology-safe-ambient",
                )

        if time.perf_counter() >= deadline and wrapper.poll() is None:
            timed_out = True
            if target_pid is not None and process_is_alive(target_pid):
                if prearm is None or not prearm.get("dumpSucceeded", False):
                    prearm = base.capture_dump(
                        target_pid,
                        dump_tool,
                        out / "timeout-evidence",
                        "phase-timeout-powershell-topology-safe-ambient",
                    )
                kill_target_tree(target_pid, out / "taskkill.log")
            wrapper.kill()
            break

    try:
        wrapper.wait(timeout=10)
    except subprocess.TimeoutExpired:
        timed_out = True
        if target_pid is not None and process_is_alive(target_pid):
            if prearm is None or not prearm.get("dumpSucceeded", False):
                prearm = base.capture_dump(
                    target_pid,
                    dump_tool,
                    out / "post-loop-evidence",
                    "target-still-alive-after-observer-loop-safe-ambient",
                )
            kill_target_tree(target_pid, out / "taskkill-post-loop.log")
        wrapper.kill()
        wrapper.wait()

    for thread in threads:
        thread.join(timeout=2)

    duration_ms = round((time.perf_counter() - started) * 1000, 3)
    confirmed = watchdog and bool(
        (prearm and prearm.get("dumpSucceeded"))
        or (exact and exact.get("dumpSucceeded"))
    )
    execution_valid = (
        not timed_out
        and wrapper.returncode == 0
        and target_pid is not None
        and target_exit_code == 0
        and json_seen_utc is not None
        and not reader_errors
    )
    result = {
        "iteration": iteration,
        "launcherProcessId": wrapper.pid,
        "targetProcessId": target_pid,
        "launcherExitCode": wrapper.returncode,
        "targetExitCode": target_exit_code,
        "durationMs": duration_ms,
        "assemblyInfoJsonSeenAtUtc": json_seen_utc,
        "instrumentationValid": target_pid is not None and json_seen_utc is not None and not reader_errors,
        "executionValid": execution_valid,
        "readerErrors": reader_errors,
        "alive500msAfterJson": bool(prearm and prearm.get("dumpSucceeded", False)),
        "watchdogSeen": watchdog,
        "watchdogSeenAtUtc": watchdog_utc,
        "timedOut": timed_out,
        "prearmCapture": prearm,
        "exactCapture": exact,
        "confirmedWatchdogDump": confirmed,
    }
    base.write_json(out / "result.json", result)
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--launcher", type=Path, required=True)
    parser.add_argument("--dump-tool", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--max-iterations", type=int, default=2000)
    parser.add_argument("--duration-minutes", type=int, default=60)
    parser.add_argument("--timeout-seconds", type=int, default=30)
    args = parser.parse_args()

    repo = args.repo.resolve()
    out = args.out.resolve()
    launcher = args.launcher.resolve()
    out.mkdir(parents=True, exist_ok=True)

    sha = subprocess.check_output(
        ["git", "-C", str(repo), "rev-parse", "HEAD"], text=True
    ).strip()
    if sha != base.TARGET_SHA:
        raise RuntimeError(f"target SHA mismatch: {sha}")
    if not launcher.is_file():
        raise RuntimeError(f"missing launcher: {launcher}")

    dll = (
        repo
        / "tests"
        / base.ASSEMBLY
        / "bin"
        / "Release"
        / "net10.0"
        / f"{base.ASSEMBLY}.dll"
    )
    if not dll.is_file():
        raise RuntimeError(f"missing target: {dll}")

    selected_env_names = sorted(
        key for key in historical_safe_child_env()
        if key not in base.sanitized_child_env()
    )
    base.write_json(out / "ambient-environment.json", {
        "selectedVariableNames": selected_env_names,
        "valuesPublished": False,
    })

    deadline = time.monotonic() + args.duration_minutes * 60
    results: list[dict] = []
    for iteration in range(1, args.max_iterations + 1):
        if time.monotonic() >= deadline:
            break
        result = run_once(
            dll,
            repo,
            launcher,
            args.dump_tool.resolve(),
            out / "iterations" / f"iteration-{iteration:06d}",
            iteration,
            args.timeout_seconds,
        )
        results.append(result)
        if not result["executionValid"] or result["watchdogSeen"]:
            break

    summary = {
        "schemaVersion": 2,
        "targetSha": base.TARGET_SHA,
        "assembly": base.ASSEMBLY,
        "phase": "assembly-info",
        "launchTopology": "Python observer -> pwsh -> System.Diagnostics.Process -> dotnet target",
        "ambientEnvironment": "sanitized baseline plus non-secret DOTNET/CLR/host/CI variables",
        "selectedAmbientVariableNames": selected_env_names,
        "targetInvocation": "dotnet DownKyi.Desktop.Tests.dll -assemblyInfo",
        "iterations": len(results),
        "jsonDetections": sum(1 for result in results if result["assemblyInfoJsonSeenAtUtc"]),
        "invalidExecutions": sum(1 for result in results if not result["executionValid"]),
        "nonzeroTargetExits": sum(1 for result in results if result["targetExitCode"] not in (0,)),
        "readerFailures": sum(1 for result in results if result["readerErrors"]),
        "alive500msAfterJson": sum(1 for result in results if result["alive500msAfterJson"]),
        "watchdogs": sum(1 for result in results if result["watchdogSeen"]),
        "confirmedWatchdogDumps": sum(1 for result in results if result["confirmedWatchdogDump"]),
        "timeouts": sum(1 for result in results if result["timedOut"]),
        "generatedAtUtc": base.now(),
    }
    base.write_json(out / "summary.json", summary)
    print(json.dumps(summary, indent=2))

    if summary["invalidExecutions"]:
        return 4
    if summary["watchdogs"] and not summary["confirmedWatchdogDumps"]:
        return 3
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
