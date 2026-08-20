#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import queue
import shutil
import subprocess
import threading
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

from issue_183_exact_watchdog_capture import WATCHDOG, capture_exact_signal, run_tool_to_file, run_text

HISTORICAL_SHA = "0ef53f0b3570dd5b5fe8f6fe6eda52e5f9badeb8"
ASSEMBLY = "DownKyi.Core.Tests"
METHOD = "DownKyi.Core.Tests.AriaClientSecurityTests.RemoteHttpsRpcRejectsAnUntrustedCertificate"


def iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def append_jsonl(path: Path, value: object) -> None:
    with path.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")


def reader(name: str, stream, output: Path, events: queue.Queue) -> None:
    with output.open("w", encoding="utf-8", newline="") as target:
        for line in iter(stream.readline, ""):
            target.write(line)
            target.flush()
            events.put((name, line.rstrip("\r\n"), datetime.now(timezone.utc), time.perf_counter_ns()))
    events.put((name, None, datetime.now(timezone.utc), time.perf_counter_ns()))


def capture_hang(process: subprocess.Popen, root: Path, dump_tool: Path) -> dict[str, object]:
    evidence = root / "hang-evidence"
    evidence.mkdir(parents=True, exist_ok=True)
    dump = evidence / "process-full.dmp"
    log = evidence / "process-full-dump.log"
    started = iso()
    exit_code = run_tool_to_file(
        [str(dump_tool), "collect", "--type", "Full", "--process-id", str(process.pid), "--output", str(dump)],
        log,
        timeout=None,
    )
    size = dump.stat().st_size if dump.exists() else 0
    succeeded = exit_code == 0 and size > 0
    if succeeded:
        run_tool_to_file(
            [str(dump_tool), "analyze", str(dump), "-c", "clrthreads", "-c", "threadpool", "-c", "clrstack -all", "-c", "exit"],
            evidence / "sos-analysis.txt",
            timeout=90,
        )
    metadata = {
        "reason": "focused-candidate-timeout-before-kill",
        "processId": process.pid,
        "captureStartedAtUtc": started,
        "dumpExitCode": exit_code,
        "dumpSucceeded": succeeded,
        "dumpSizeBytes": size,
        "captureFinishedAtUtc": iso(),
    }
    write_json(evidence / "hang-capture.json", metadata)
    return metadata


def run_candidate(command: list[str], cwd: Path, root: Path, dump_tool: Path, iteration: int, timeout_seconds: int) -> dict[str, object]:
    root.mkdir(parents=True, exist_ok=True)
    creationflags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    process = subprocess.Popen(
        command,
        cwd=str(cwd),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
        creationflags=creationflags,
    )
    assert process.stdout is not None and process.stderr is not None
    events: queue.Queue = queue.Queue()
    readers = [
        threading.Thread(target=reader, args=("stdout", process.stdout, root / "stdout.txt", events), daemon=True),
        threading.Thread(target=reader, args=("stderr", process.stderr, root / "stderr.txt", events), daemon=True),
    ]
    for item in readers:
        item.start()

    deadline = time.monotonic() + timeout_seconds
    done: set[str] = set()
    watchdog = False
    capture = None
    hang = None
    while process.poll() is None or len(done) < 2 or not events.empty():
        try:
            stream_name, line, observed_at, observed_perf_ns = events.get(timeout=0.005)
            if line is None:
                done.add(stream_name)
            elif not watchdog and WATCHDOG in line:
                watchdog = True
                capture = capture_exact_signal(
                    process,
                    root / "exact-watchdog-evidence",
                    dump_tool,
                    ASSEMBLY,
                    iteration,
                    stream_name,
                    line,
                    observed_at,
                    observed_perf_ns,
                )
        except queue.Empty:
            pass

        if process.poll() is None and time.monotonic() >= deadline:
            # Fail closed: capture the still-live process before termination. This is not #183 success
            # unless the exact watchdog was also observed in this execution.
            hang = capture_hang(process, root, dump_tool)
            process.kill()
            break

    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        if process.poll() is None and hang is None:
            hang = capture_hang(process, root, dump_tool)
        process.kill()
        process.wait()

    for item in readers:
        item.join(timeout=2)

    return {
        "iteration": iteration,
        "processId": process.pid,
        "exitCode": process.returncode,
        "watchdogSeen": watchdog,
        "watchdogDumpSucceeded": bool(capture and capture.dump_succeeded),
        "watchdogDumpSizeBytes": capture.dump_size_bytes if capture else 0,
        "timedOut": hang is not None,
        "hangDumpSucceeded": bool(hang and hang.get("dumpSucceeded")),
        "observedAtUtc": iso(),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--dump-tool", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--duration-minutes", type=int, default=110)
    parser.add_argument("--max-iterations", type=int, default=3000)
    parser.add_argument("--timeout-seconds", type=int, default=180)
    args = parser.parse_args()

    repo = args.repo.resolve()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    actual_sha = run_text(["git", "-C", str(repo), "rev-parse", "HEAD"], repo).strip()
    if actual_sha != HISTORICAL_SHA:
        raise RuntimeError(f"Historical SHA mismatch: {actual_sha}")
    assembly = repo / "tests" / ASSEMBLY / "bin" / "Release" / "net10.0" / f"{ASSEMBLY}.dll"
    if not assembly.is_file():
        raise RuntimeError(f"Assembly missing: {assembly}")

    started = datetime.now(timezone.utc)
    deadline = started + timedelta(minutes=args.duration_minutes)
    jsonl = out / "iterations.jsonl"
    retained = out / "retained"
    work = out / "work"
    retained.mkdir(exist_ok=True)
    work.mkdir(exist_ok=True)
    iterations = watchdogs = captures = hangs = failures = 0

    while datetime.now(timezone.utc) < deadline and iterations < args.max_iterations and captures == 0:
        iterations += 1
        root = work / f"iteration-{iterations:06d}"
        marker = root / "execution.lifecycle"
        result = run_candidate(
            [
                "dotnet", str(assembly),
                "-automated", "-noLogo", "-noColor", "-parallel", "none",
                "-method", METHOD,
            ],
            repo,
            root,
            args.dump_tool.resolve(),
            iterations,
            args.timeout_seconds,
        )
        append_jsonl(jsonl, result)
        watchdogs += int(result["watchdogSeen"])
        captures += int(result["watchdogSeen"] and result["watchdogDumpSucceeded"])
        hangs += int(result["timedOut"])
        abnormal = result["timedOut"] or result["exitCode"] != 0 or result["watchdogSeen"]
        if abnormal:
            destination = retained / root.name
            if destination.exists():
                shutil.rmtree(destination)
            shutil.move(str(root), str(destination))
        else:
            shutil.rmtree(root, ignore_errors=True)
        if result["watchdogSeen"] and not result["watchdogDumpSucceeded"]:
            failures += 1
            break
        if iterations % 100 == 0:
            print(f"PROGRESS iterations={iterations} watchdogs={watchdogs} captures={captures} hangs={hangs}", flush=True)

    summary = {
        "schemaVersion": 1,
        "historicalSha": HISTORICAL_SHA,
        "assembly": ASSEMBLY,
        "method": METHOD,
        "iterations": iterations,
        "watchdogExecutions": watchdogs,
        "confirmedCaptures": captures,
        "hangCaptures": hangs,
        "instrumentationFailures": failures,
        "startedAtUtc": started.isoformat().replace("+00:00", "Z"),
        "finishedAtUtc": iso(),
    }
    write_json(out / "summary.json", summary)
    print(json.dumps(summary, indent=2))
    if watchdogs and not captures:
        return 3
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
