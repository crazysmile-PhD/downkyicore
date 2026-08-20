#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import queue
import subprocess
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

WATCHDOG = "Waiting 10 seconds for foreground threads to exit"
TARGET_SHA = "75bff22801972ada7cfd24be595b0753780a7592"
ASSEMBLY = "DownKyi.Desktop.Tests"


def now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def run_tool(command: list[str], log: Path, timeout: float | None = None) -> int:
    try:
        cp = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace", timeout=timeout, check=False)
        log.write_text(cp.stdout or "", encoding="utf-8")
        return cp.returncode
    except subprocess.TimeoutExpired as exc:
        text = exc.stdout or ""
        if isinstance(text, bytes):
            text = text.decode("utf-8", errors="replace")
        log.write_text(text + "\nTOOL TIMEOUT\n", encoding="utf-8")
        return -3
    except Exception as exc:
        log.write_text(f"tool invocation failed: {exc!r}\n", encoding="utf-8")
        return -1


def capture_dump(pid: int, dump_tool: Path, out: Path, reason: str) -> dict:
    out.mkdir(parents=True, exist_ok=True)
    dump = out / "process-full.dmp"
    log = out / "dotnet-dump.log"
    analysis = out / "sos-analysis.txt"
    started = time.perf_counter()
    exit_code = run_tool([
        str(dump_tool), "collect", "--type", "Full", "--process-id", str(pid), "--output", str(dump)
    ], log, timeout=90)
    size = dump.stat().st_size if dump.exists() else 0
    ok = exit_code == 0 and size > 0
    if ok:
        run_tool([
            str(dump_tool), "analyze", str(dump),
            "-c", "clrthreads",
            "-c", "threadpool",
            "-c", "clrstack -all",
            "-c", "dumpheap -stat",
            "-c", "exit"
        ], analysis, timeout=120)
    result = {
        "reason": reason,
        "processId": pid,
        "capturedAtUtc": now(),
        "dumpExitCode": exit_code,
        "dumpSucceeded": ok,
        "dumpSizeBytes": size,
        "captureDurationMs": round((time.perf_counter() - started) * 1000, 3),
        "dumpPath": str(dump),
    }
    write_json(out / "capture.json", result)
    return result


def reader(name: str, stream, path: Path, events: queue.Queue) -> None:
    with path.open("w", encoding="utf-8", newline="") as f:
        for line in iter(stream.readline, ""):
            f.write(line)
            f.flush()
            events.put((name, line.rstrip("\r\n"), time.perf_counter(), now()))
    events.put((name, None, time.perf_counter(), now()))


def run_once(dll: Path, cwd: Path, dump_tool: Path, out: Path, iteration: int, timeout_seconds: int) -> dict:
    out.mkdir(parents=True, exist_ok=True)
    p = subprocess.Popen(
        ["dotnet", str(dll), "-assemblyInfo"],
        cwd=str(cwd),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    assert p.stdout is not None and p.stderr is not None
    events: queue.Queue = queue.Queue()
    threads = [
        threading.Thread(target=reader, args=("stdout", p.stdout, out / "stdout.txt", events), daemon=True),
        threading.Thread(target=reader, args=("stderr", p.stderr, out / "stderr.txt", events), daemon=True),
    ]
    for t in threads:
        t.start()

    started = time.perf_counter()
    deadline = started + timeout_seconds
    json_seen_at: float | None = None
    json_seen_utc: str | None = None
    prearm_done = False
    watchdog = False
    watchdog_utc: str | None = None
    prearm: dict | None = None
    exact: dict | None = None
    timed_out = False
    done_streams: set[str] = set()

    while p.poll() is None or len(done_streams) < 2 or not events.empty():
        try:
            stream, line, perf, utc = events.get(timeout=0.01)
            if line is None:
                done_streams.add(stream)
            else:
                if stream == "stdout" and json_seen_at is None and line.lstrip().startswith("{"):
                    try:
                        obj = json.loads(line)
                        if isinstance(obj, dict) and "targetFramework" in obj:
                            json_seen_at = perf
                            json_seen_utc = utc
                    except json.JSONDecodeError:
                        pass
                if WATCHDOG in line and not watchdog:
                    watchdog = True
                    watchdog_utc = utc
                    if prearm is None or not prearm.get("dumpSucceeded", False):
                        exact = capture_dump(p.pid, dump_tool, out / "exact-watchdog", "exact-watchdog")
        except queue.Empty:
            pass

        if json_seen_at is not None and not prearm_done and time.perf_counter() - json_seen_at >= 0.500:
            prearm_done = True
            if p.poll() is None:
                prearm = capture_dump(p.pid, dump_tool, out / "prearm-500ms", "alive-500ms-after-assembly-info-json")

        if p.poll() is None and time.perf_counter() >= deadline:
            timed_out = True
            if prearm is None or not prearm.get("dumpSucceeded", False):
                prearm = capture_dump(p.pid, dump_tool, out / "timeout-evidence", "phase-timeout-before-termination")
            p.kill()
            break

    try:
        p.wait(timeout=10)
    except subprocess.TimeoutExpired:
        timed_out = True
        if prearm is None or not prearm.get("dumpSucceeded", False):
            prearm = capture_dump(p.pid, dump_tool, out / "post-loop-evidence", "process-still-alive-after-observer-loop")
        p.kill()
        p.wait()

    for t in threads:
        t.join(timeout=2)

    duration_ms = round((time.perf_counter() - started) * 1000, 3)
    confirmed = watchdog and bool((prearm and prearm.get("dumpSucceeded")) or (exact and exact.get("dumpSucceeded")))
    result = {
        "iteration": iteration,
        "processId": p.pid,
        "exitCode": p.returncode,
        "durationMs": duration_ms,
        "assemblyInfoJsonSeenAtUtc": json_seen_utc,
        "alive500msAfterJson": prearm_done and prearm is not None,
        "watchdogSeen": watchdog,
        "watchdogSeenAtUtc": watchdog_utc,
        "timedOut": timed_out,
        "prearmCapture": prearm,
        "exactCapture": exact,
        "confirmedWatchdogDump": confirmed,
    }
    write_json(out / "result.json", result)
    return result


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", type=Path, required=True)
    ap.add_argument("--dump-tool", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--max-iterations", type=int, default=2000)
    ap.add_argument("--duration-minutes", type=int, default=60)
    ap.add_argument("--timeout-seconds", type=int, default=30)
    args = ap.parse_args()

    repo = args.repo.resolve()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    sha = subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()
    if sha != TARGET_SHA:
        raise RuntimeError(f"target SHA mismatch: {sha}")
    dll = repo / "tests" / ASSEMBLY / "bin" / "Release" / "net10.0" / f"{ASSEMBLY}.dll"
    if not dll.is_file():
        raise RuntimeError(f"missing target: {dll}")

    deadline = time.monotonic() + args.duration_minutes * 60
    results: list[dict] = []
    for i in range(1, args.max_iterations + 1):
        if time.monotonic() >= deadline:
            break
        result = run_once(dll, repo, args.dump_tool.resolve(), out / "iterations" / f"iteration-{i:06d}", i, args.timeout_seconds)
        results.append(result)
        if result["watchdogSeen"]:
            break

    summary = {
        "schemaVersion": 1,
        "targetSha": TARGET_SHA,
        "assembly": ASSEMBLY,
        "phase": "assembly-info",
        "invocation": "dotnet DownKyi.Desktop.Tests.dll -assemblyInfo",
        "iterations": len(results),
        "alive500msAfterJson": sum(1 for r in results if r["alive500msAfterJson"]),
        "watchdogs": sum(1 for r in results if r["watchdogSeen"]),
        "confirmedWatchdogDumps": sum(1 for r in results if r["confirmedWatchdogDump"]),
        "timeouts": sum(1 for r in results if r["timedOut"]),
        "generatedAtUtc": now(),
    }
    write_json(out / "summary.json", summary)
    print(json.dumps(summary, indent=2))

    if summary["watchdogs"] and not summary["confirmedWatchdogDumps"]:
        return 3
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
