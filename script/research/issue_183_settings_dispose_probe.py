#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

HISTORICAL_SHA = "0ef53f0b3570dd5b5fe8f6fe6eda52e5f9badeb8"
ASSEMBLY = "DownKyi.Core.Tests"
METHOD = (
    "DownKyi.Core.Tests.SettingsStoreTests."
    "AsyncDisposeCancelsPendingDebounceAndFlushesWithoutWaitingForDelay"
)
WATCHDOG = "Waiting 10 seconds for foreground threads to exit"


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso(value: datetime | None = None) -> str:
    return (value or utc_now()).isoformat().replace("+00:00", "Z")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def append_jsonl(path: Path, value: object) -> None:
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")


@dataclass
class DumpResult:
    reason: str
    succeeded: bool
    exit_code: int
    size_bytes: int
    started_at_utc: str
    duration_ms: float
    path: str


@dataclass
class ObservedResult:
    process_id: int
    exit_code: int
    timed_out: bool
    watchdog_seen: bool
    dump: DumpResult | None
    duration_ms: float


def reader(name: str, stream, path: Path, events: queue.Queue) -> None:
    with path.open("w", encoding="utf-8", newline="") as out:
        for line in iter(stream.readline, ""):
            out.write(line)
            out.flush()
            events.put((name, line.rstrip("\r\n"), utc_now(), time.perf_counter_ns()))
    events.put((name, None, utc_now(), time.perf_counter_ns()))


def run_tool(command: list[str], log_path: Path, timeout: float | None) -> int:
    try:
        completed = subprocess.run(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
        log_path.write_text(completed.stdout or "", encoding="utf-8")
        return completed.returncode
    except subprocess.TimeoutExpired as exc:
        output = exc.stdout or ""
        if isinstance(output, bytes):
            output = output.decode("utf-8", errors="replace")
        log_path.write_text(output + "\nTOOL TIMEOUT\n", encoding="utf-8")
        return -3
    except Exception as exc:
        log_path.write_text(f"tool invocation failed: {exc!r}\n", encoding="utf-8")
        return -1


def analyze_dump(dump_tool: Path, dump_path: Path, analysis_path: Path) -> None:
    commands = [
        "clrthreads",
        "threadpool",
        "clrstack -all",
        "dumpheap -type DownKyi.Core.Settings.SettingsManager",
        "dumpheap -type DownKyi.Core.Tests.SettingsStoreTests+ControlledDelay",
        "dumpheap -type System.Threading.CancellationTokenSource",
        "dumpheap -type System.Threading.SemaphoreSlim",
        "dumpheap -type System.Threading.Tasks.Task",
        "exit",
    ]
    command = [str(dump_tool), "analyze", str(dump_path)]
    for item in commands:
        command += ["-c", item]
    run_tool(command, analysis_path, timeout=120)


def capture_dump(
    process: subprocess.Popen,
    dump_tool: Path,
    evidence_dir: Path,
    reason: str,
    *,
    observed_stream: str | None = None,
    observed_line: str | None = None,
    observed_at: datetime | None = None,
    observed_perf_ns: int | None = None,
) -> DumpResult:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    dump_path = evidence_dir / "process-full.dmp"
    log_path = evidence_dir / "process-full-dump.log"
    metadata_path = evidence_dir / "capture.json"
    analysis_path = evidence_dir / "sos-analysis.txt"

    started = utc_now()
    started_perf = time.perf_counter_ns()
    alive = process.poll() is None
    trigger_latency_ms = None
    if observed_perf_ns is not None:
        trigger_latency_ms = (started_perf - observed_perf_ns) / 1_000_000.0

    metadata = {
        "schemaVersion": 5,
        "reason": reason,
        "assembly": ASSEMBLY,
        "method": METHOD,
        "processId": process.pid,
        "processAliveAtCaptureStart": alive,
        "captureStartedAtUtc": iso(started),
        "observedStream": observed_stream,
        "observedLine": observed_line,
        "signalObservedAtUtc": iso(observed_at) if observed_at else None,
        "triggerLatencyMs": round(trigger_latency_ms, 3) if trigger_latency_ms is not None else None,
        "dumpSucceeded": False,
    }
    write_json(metadata_path, metadata)

    if alive:
        exit_code = run_tool(
            [
                str(dump_tool),
                "collect",
                "--type",
                "Full",
                "--process-id",
                str(process.pid),
                "--output",
                str(dump_path),
            ],
            log_path,
            timeout=90,
        )
    else:
        exit_code = -2
        log_path.write_text("Target exited before dump capture started.\n", encoding="utf-8")

    size = dump_path.stat().st_size if dump_path.exists() else 0
    succeeded = exit_code == 0 and size > 0
    finished = utc_now()
    duration_ms = (finished - started).total_seconds() * 1000.0
    metadata.update(
        {
            "dumpExitCode": exit_code,
            "dumpSucceeded": succeeded,
            "dumpSizeBytes": size,
            "captureFinishedAtUtc": iso(finished),
            "captureDurationMs": round(duration_ms, 3),
        }
    )
    write_json(metadata_path, metadata)

    return DumpResult(
        reason=reason,
        succeeded=succeeded,
        exit_code=exit_code,
        size_bytes=size,
        started_at_utc=iso(started),
        duration_ms=round(duration_ms, 3),
        path=str(dump_path),
    )


def run_observed(
    command: list[str],
    cwd: Path,
    stdout_path: Path,
    stderr_path: Path,
    dump_tool: Path,
    evidence_dir: Path,
    hang_threshold_seconds: float,
    environment: dict[str, str] | None = None,
) -> ObservedResult:
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    if environment:
        env.update(environment)

    creationflags = 0
    if os.name == "nt" and hasattr(subprocess, "CREATE_NO_WINDOW"):
        creationflags = subprocess.CREATE_NO_WINDOW

    started_perf = time.perf_counter_ns()
    process = subprocess.Popen(
        command,
        cwd=str(cwd),
        env=env,
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
        threading.Thread(target=reader, args=("stdout", process.stdout, stdout_path, events), daemon=True),
        threading.Thread(target=reader, args=("stderr", process.stderr, stderr_path, events), daemon=True),
    ]
    for item in readers:
        item.start()

    deadline = time.monotonic() + hang_threshold_seconds
    done_streams: set[str] = set()
    watchdog_seen = False
    timed_out = False
    dump: DumpResult | None = None

    while process.poll() is None or len(done_streams) < 2 or not events.empty():
        try:
            stream_name, line, observed_at, observed_perf_ns = events.get(timeout=0.005)
            if line is None:
                done_streams.add(stream_name)
                continue
            if dump is None and WATCHDOG in line:
                watchdog_seen = True
                dump = capture_dump(
                    process,
                    dump_tool,
                    evidence_dir,
                    "exact-xunit-foreground-thread-watchdog",
                    observed_stream=stream_name,
                    observed_line=line,
                    observed_at=observed_at,
                    observed_perf_ns=observed_perf_ns,
                )
        except queue.Empty:
            pass

        if process.poll() is None and dump is None and time.monotonic() >= deadline:
            timed_out = True
            dump = capture_dump(
                process,
                dump_tool,
                evidence_dir,
                "target-method-hang-timeout",
            )

        if process.poll() is None and dump is not None:
            try:
                process.kill()
            except ProcessLookupError:
                pass

        if process.poll() is not None and len(done_streams) >= 2 and events.empty():
            break

    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
        timed_out = True

    for item in readers:
        item.join(timeout=2)

    if dump is not None and dump.succeeded:
        analyze_dump(dump_tool, Path(dump.path), evidence_dir / "sos-analysis.txt")

    duration_ms = (time.perf_counter_ns() - started_perf) / 1_000_000.0
    return ObservedResult(
        process_id=process.pid,
        exit_code=process.returncode,
        timed_out=timed_out,
        watchdog_seen=watchdog_seen,
        dump=dump,
        duration_ms=round(duration_ms, 3),
    )


def self_test(dump_tool: Path, output: Path) -> int:
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)
    pwsh = shutil.which("pwsh") or shutil.which("pwsh.exe")
    if not pwsh:
        raise RuntimeError("pwsh was not found")

    exact_text = WATCHDOG.replace("'", "''")
    exact = run_observed(
        [pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", f"Write-Output '{exact_text}'; Start-Sleep -Seconds 10"],
        output,
        output / "exact.stdout.txt",
        output / "exact.stderr.txt",
        dump_tool,
        output / "exact-evidence",
        hang_threshold_seconds=30,
    )
    timeout = run_observed(
        [pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 10"],
        output,
        output / "timeout.stdout.txt",
        output / "timeout.stderr.txt",
        dump_tool,
        output / "timeout-evidence",
        hang_threshold_seconds=1.0,
    )

    exact_ok = exact.watchdog_seen and exact.dump is not None and exact.dump.succeeded
    timeout_ok = timeout.timed_out and timeout.dump is not None and timeout.dump.succeeded
    summary = {
        "schemaVersion": 5,
        "passed": exact_ok and timeout_ok,
        "exactWatchdogPathPassed": exact_ok,
        "timeoutDumpPathPassed": timeout_ok,
        "exactDumpBytes": exact.dump.size_bytes if exact.dump else 0,
        "timeoutDumpBytes": timeout.dump.size_bytes if timeout.dump else 0,
        "generatedAtUtc": iso(),
    }
    write_json(output / "self-test.json", summary)

    for result in (exact, timeout):
        if result.dump:
            path = Path(result.dump.path)
            if path.exists():
                path.unlink()

    print(f"SELF-TEST: passed={summary['passed']} exact={exact_ok} timeout={timeout_ok}")
    return 0 if summary["passed"] else 2


def run_text(command: list[str], cwd: Path) -> str:
    completed = subprocess.run(
        command,
        cwd=str(cwd),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(completed.stderr)
    return completed.stdout


def stress(
    repo: Path,
    dump_tool: Path,
    output: Path,
    duration_minutes: int,
    max_iterations: int,
    hang_threshold_seconds: float,
) -> int:
    repo = repo.resolve()
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    actual_sha = run_text(["git", "-C", str(repo), "rev-parse", "HEAD"], repo).strip()
    if actual_sha != HISTORICAL_SHA:
        raise RuntimeError(f"Historical SHA mismatch: {actual_sha}")

    assembly = repo / "tests" / ASSEMBLY / "bin" / "Release" / "net10.0" / f"{ASSEMBLY}.dll"
    if not assembly.is_file():
        raise RuntimeError(f"Historical test assembly is missing: {assembly}")

    started = utc_now()
    deadline = started + timedelta(minutes=duration_minutes)
    iterations = hangs = watchdogs = captures = unexpected_failures = 0
    jsonl = output / "iterations.jsonl"
    work = output / "work"
    retained = output / "retained"
    work.mkdir(exist_ok=True)
    retained.mkdir(exist_ok=True)

    fatal: str | None = None
    while utc_now() < deadline and iterations < max_iterations and captures == 0:
        iterations += 1
        name = f"iteration-{iterations:07d}"
        root = work / name
        root.mkdir(parents=True)
        marker = root / "execution.lifecycle"
        evidence = root / "evidence"

        result = run_observed(
            [
                "dotnet",
                str(assembly),
                "-automated",
                "-noLogo",
                "-noColor",
                "-parallel",
                "none",
                "-method",
                METHOD,
            ],
            repo,
            root / "execution.stdout.txt",
            root / "execution.stderr.txt",
            dump_tool,
            evidence,
            hang_threshold_seconds,
            environment={"DOWNKYI_LIFECYCLE_MARKER": str(marker)},
        )

        captured = result.dump is not None and result.dump.succeeded
        if result.timed_out:
            hangs += 1
        if result.watchdog_seen:
            watchdogs += 1
        if captured:
            captures += 1
        if not result.timed_out and not result.watchdog_seen and result.exit_code != 0:
            unexpected_failures += 1

        row = {
            "iteration": iterations,
            "processId": result.process_id,
            "durationMs": result.duration_ms,
            "exitCode": result.exit_code,
            "timedOut": result.timed_out,
            "watchdogSeen": result.watchdog_seen,
            "dumpReason": result.dump.reason if result.dump else None,
            "dumpSucceeded": captured,
            "dumpSizeBytes": result.dump.size_bytes if result.dump else 0,
            "observedAtUtc": iso(),
        }
        append_jsonl(jsonl, row)

        if result.timed_out or result.watchdog_seen or result.exit_code != 0 or captured:
            shutil.move(str(root), str(retained / name))
        else:
            shutil.rmtree(root, ignore_errors=True)

        if result.timed_out and not captured:
            fatal = "Target method hung but Full dump capture failed; stop instead of blind repetition."
            break
        if result.watchdog_seen and not captured:
            fatal = "Exact watchdog appeared but Full dump capture failed; stop instead of blind repetition."
            break

        if iterations % 250 == 0:
            print(
                f"PROGRESS iterations={iterations} hangs={hangs} watchdogs={watchdogs} "
                f"captures={captures} unexpectedFailures={unexpected_failures}",
                flush=True,
            )

    summary = {
        "schemaVersion": 5,
        "historicalSha": HISTORICAL_SHA,
        "assembly": ASSEMBLY,
        "method": METHOD,
        "startedAtUtc": iso(started),
        "finishedAtUtc": iso(),
        "iterations": iterations,
        "hangThresholdSeconds": hang_threshold_seconds,
        "hangExecutions": hangs,
        "watchdogExecutions": watchdogs,
        "confirmedCaptures": captures,
        "unexpectedFailures": unexpected_failures,
        "fatal": fatal,
        "success": captures > 0,
    }
    write_json(output / "summary.json", summary)
    print(json.dumps(summary, separators=(",", ":")), flush=True)
    return 3 if fatal else 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path)
    parser.add_argument("--dump-tool", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--duration-minutes", type=int, default=90)
    parser.add_argument("--max-iterations", type=int, default=100000)
    parser.add_argument("--hang-threshold-seconds", type=float, default=15.0)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    dump_tool = args.dump_tool.resolve()
    if not dump_tool.is_file():
        raise RuntimeError(f"dotnet-dump was not found: {dump_tool}")
    if args.self_test:
        return self_test(dump_tool, args.out.resolve())
    if args.repo is None:
        parser.error("--repo is required unless --self-test is used")
    if args.duration_minutes <= 0 or args.max_iterations <= 0 or args.hang_threshold_seconds <= 0:
        parser.error("duration/max-iterations/hang-threshold must be positive")
    return stress(
        args.repo,
        dump_tool,
        args.out,
        args.duration_minutes,
        args.max_iterations,
        args.hang_threshold_seconds,
    )


if __name__ == "__main__":
    raise SystemExit(main())
