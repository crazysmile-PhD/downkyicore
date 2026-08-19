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

WATCHDOG = "Waiting 10 seconds for foreground threads to exit"
HISTORICAL_SHA = "0ef53f0b3570dd5b5fe8f6fe6eda52e5f9badeb8"
ASSEMBLY = "DownKyi.Core.Tests"


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso(dt: datetime | None = None) -> str:
    return (dt or utc_now()).isoformat().replace("+00:00", "Z")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


@dataclass
class CaptureResult:
    dump_succeeded: bool
    dump_size_bytes: int
    dump_exit_code: int
    trigger_latency_ms: float
    capture_duration_ms: float
    dump_path: str
    metadata_path: str


@dataclass
class RunResult:
    process_id: int
    exit_code: int
    timed_out: bool
    watchdog_seen: bool
    capture: CaptureResult | None
    duration_ms: float


def _reader(stream_name: str, stream, output_path: Path, events: queue.Queue) -> None:
    with output_path.open("w", encoding="utf-8", newline="") as out:
        for line in iter(stream.readline, ""):
            out.write(line)
            out.flush()
            events.put((stream_name, line.rstrip("\r\n"), utc_now(), time.perf_counter_ns()))
    events.put((stream_name, None, utc_now(), time.perf_counter_ns()))


def run_tool_to_file(command: list[str], output_path: Path, timeout: float | None = None) -> int:
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
        output_path.write_text(completed.stdout or "", encoding="utf-8")
        return completed.returncode
    except subprocess.TimeoutExpired as exc:
        text = (exc.stdout or "") + "\nANALYSIS TIMEOUT\n"
        output_path.write_text(text, encoding="utf-8")
        return -3
    except Exception as exc:
        output_path.write_text(f"tool invocation failed: {exc!r}\n", encoding="utf-8")
        return -1


def capture_exact_signal(
    process: subprocess.Popen,
    evidence_dir: Path,
    dump_tool: Path,
    assembly: str,
    iteration: int,
    observed_stream: str,
    observed_line: str,
    observed_at: datetime,
    observed_perf_ns: int,
) -> CaptureResult:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    dump_path = evidence_dir / "process-full.dmp"
    dump_log = evidence_dir / "process-full-dump.log"
    analysis_log = evidence_dir / "sos-analysis.txt"
    metadata_path = evidence_dir / "exact-signal.json"

    capture_started = utc_now()
    capture_perf_ns = time.perf_counter_ns()
    latency_ms = (capture_perf_ns - observed_perf_ns) / 1_000_000.0
    alive_at_start = process.poll() is None
    write_json(
        metadata_path,
        {
            "schemaVersion": 4,
            "reason": "exact-xunit-foreground-thread-watchdog",
            "watchdogText": WATCHDOG,
            "assembly": assembly,
            "iteration": iteration,
            "processId": process.pid,
            "observedStream": observed_stream,
            "observedLine": observed_line,
            "signalObservedAtUtc": iso(observed_at),
            "captureStartedAtUtc": iso(capture_started),
            "triggerLatencyMs": round(latency_ms, 3),
            "processAliveAtCaptureStart": alive_at_start,
            "dumpSucceeded": False,
        },
    )

    if alive_at_start:
        dump_exit = run_tool_to_file(
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
            dump_log,
            timeout=None,
        )
    else:
        dump_exit = -2
        dump_log.write_text("Target exited before dotnet-dump could start.\n", encoding="utf-8")

    dump_size = dump_path.stat().st_size if dump_path.exists() else 0
    succeeded = dump_exit == 0 and dump_size > 0
    if succeeded:
        run_tool_to_file(
            [
                str(dump_tool),
                "analyze",
                str(dump_path),
                "-c",
                "clrthreads",
                "-c",
                "threadpool",
                "-c",
                "clrstack -all",
                "-c",
                "exit",
            ],
            analysis_log,
            timeout=90,
        )

    finished = utc_now()
    duration_ms = (finished - capture_started).total_seconds() * 1000.0
    metadata = {
        "schemaVersion": 4,
        "reason": "exact-xunit-foreground-thread-watchdog",
        "watchdogText": WATCHDOG,
        "assembly": assembly,
        "iteration": iteration,
        "processId": process.pid,
        "observedStream": observed_stream,
        "observedLine": observed_line,
        "signalObservedAtUtc": iso(observed_at),
        "captureStartedAtUtc": iso(capture_started),
        "triggerLatencyMs": round(latency_ms, 3),
        "processAliveAtCaptureStart": alive_at_start,
        "dumpExitCode": dump_exit,
        "dumpSucceeded": succeeded,
        "dumpSizeBytes": dump_size,
        "captureFinishedAtUtc": iso(finished),
        "captureDurationMs": round(duration_ms, 3),
    }
    write_json(metadata_path, metadata)
    return CaptureResult(
        succeeded,
        dump_size,
        dump_exit,
        round(latency_ms, 3),
        round(duration_ms, 3),
        str(dump_path),
        str(metadata_path),
    )


def run_observed(
    command: list[str],
    cwd: Path,
    stdout_path: Path,
    stderr_path: Path,
    timeout_seconds: int,
    dump_tool: Path,
    *,
    environment: dict[str, str] | None = None,
    watch_for_watchdog: bool = False,
    evidence_dir: Path | None = None,
    assembly: str = "self-test",
    iteration: int = 0,
) -> RunResult:
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    if environment:
        env.update(environment)

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
    )
    assert process.stdout is not None and process.stderr is not None

    events: queue.Queue = queue.Queue()
    readers = [
        threading.Thread(target=_reader, args=("stdout", process.stdout, stdout_path, events), daemon=True),
        threading.Thread(target=_reader, args=("stderr", process.stderr, stderr_path, events), daemon=True),
    ]
    for reader in readers:
        reader.start()

    done_streams: set[str] = set()
    deadline = time.monotonic() + timeout_seconds
    timed_out = False
    watchdog_seen = False
    capture: CaptureResult | None = None

    while process.poll() is None or len(done_streams) < 2 or not events.empty():
        try:
            stream_name, line, observed_at, observed_perf_ns = events.get(timeout=0.005)
            if line is None:
                done_streams.add(stream_name)
                continue
            if watch_for_watchdog and not watchdog_seen and WATCHDOG in line:
                watchdog_seen = True
                capture = capture_exact_signal(
                    process,
                    evidence_dir or stdout_path.parent / "exact-watchdog-evidence",
                    dump_tool,
                    assembly,
                    iteration,
                    stream_name,
                    line,
                    observed_at,
                    observed_perf_ns,
                )
        except queue.Empty:
            pass

        if process.poll() is None and time.monotonic() >= deadline:
            timed_out = True
            process.kill()
            break

    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
        timed_out = True

    for reader in readers:
        reader.join(timeout=2)

    duration_ms = (time.perf_counter_ns() - started_perf) / 1_000_000.0
    return RunResult(process.pid, process.returncode, timed_out, watchdog_seen, capture, round(duration_ms, 3))


def require_success(name: str, result: RunResult) -> None:
    if result.timed_out or result.exit_code != 0:
        raise RuntimeError(f"{name} failed: exit={result.exit_code} timedOut={result.timed_out}")


def self_test(dump_tool: Path, output: Path) -> int:
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)
    pwsh = shutil.which("pwsh") or shutil.which("pwsh.exe")
    if not pwsh:
        raise RuntimeError("pwsh was not found for exact-signal self-test")

    escaped = WATCHDOG.replace("'", "''")
    result = run_observed(
        [pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", f"Write-Output '{escaped}'; Start-Sleep -Seconds 12"],
        output,
        output / "stdout.txt",
        output / "stderr.txt",
        30,
        dump_tool,
        watch_for_watchdog=True,
        evidence_dir=output / "exact-signal-evidence",
        assembly="Gate.ExactSignal",
        iteration=1,
    )
    passed = result.watchdog_seen and result.capture is not None and result.capture.dump_succeeded
    summary = {
        "schemaVersion": 4,
        "passed": passed,
        "processId": result.process_id,
        "exitCode": result.exit_code,
        "timedOut": result.timed_out,
        "watchdogSeen": result.watchdog_seen,
        "dumpSucceeded": bool(result.capture and result.capture.dump_succeeded),
        "dumpSizeBytes": result.capture.dump_size_bytes if result.capture else 0,
        "triggerLatencyMs": result.capture.trigger_latency_ms if result.capture else None,
        "captureDurationMs": result.capture.capture_duration_ms if result.capture else None,
        "generatedAtUtc": iso(),
    }
    write_json(output / "self-test.json", summary)
    if result.capture:
        dump = Path(result.capture.dump_path)
        if dump.exists():
            dump.unlink()
    print(
        f"EXACT CAPTURE SELF-TEST: passed={passed} watchdog={result.watchdog_seen} "
        f"dump={bool(result.capture and result.capture.dump_succeeded)}"
    )
    return 0 if passed else 2


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


def append_jsonl(path: Path, value: object) -> None:
    with path.open("a", encoding="utf-8") as out:
        out.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")


def write_summary(
    output: Path,
    started: datetime,
    iterations: int,
    watchdogs: int,
    captures: int,
    phase_failures: int,
    fatal: str | None,
) -> None:
    write_json(
        output / "summary.json",
        {
            "schemaVersion": 4,
            "historicalSha": HISTORICAL_SHA,
            "assembly": ASSEMBLY,
            "startedAtUtc": iso(started),
            "finishedAtUtc": iso(),
            "iterations": iterations,
            "watchdogExecutions": watchdogs,
            "confirmedCaptures": captures,
            "phaseFailures": phase_failures,
            "fatal": fatal,
            "success": captures > 0,
        },
    )


def stress(repo: Path, dump_tool: Path, output: Path, duration_minutes: int, max_iterations: int) -> int:
    repo = repo.resolve()
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    actual_sha = run_text(["git", "-C", str(repo), "rev-parse", "HEAD"], repo).strip()
    if actual_sha != HISTORICAL_SHA:
        raise RuntimeError(f"Historical SHA mismatch: {actual_sha}")

    assembly = repo / "tests" / ASSEMBLY / "bin" / "Release" / "net10.0" / f"{ASSEMBLY}.dll"
    probe = repo / "tools" / "DownKyi.AssemblyLifecycleProbe" / "bin" / "Release" / "net10.0" / "DownKyi.AssemblyLifecycleProbe.dll"
    if not assembly.is_file() or not probe.is_file():
        raise RuntimeError("Historical target assembly or lifecycle probe is missing")

    retained = output / "retained"
    inconclusive = output / "inconclusive"
    work = output / "work"
    for path in (retained, inconclusive, work):
        path.mkdir(parents=True, exist_ok=True)

    started = utc_now()
    deadline = started + timedelta(minutes=duration_minutes)
    iteration = watchdogs = captures = phase_failures = 0
    jsonl = output / "iterations.jsonl"

    while utc_now() < deadline and iteration < max_iterations and captures == 0:
        iteration += 1
        name = f"iteration-{iteration:06d}"
        iteration_root = work / name
        iteration_root.mkdir(parents=True)
        failure: str | None = None
        execution: RunResult | None = None

        try:
            load = run_observed(
                ["dotnet", str(probe), "--assembly", str(assembly)], repo,
                iteration_root / "load.stdout.txt", iteration_root / "load.stderr.txt", 180, dump_tool,
            )
            require_success("load", load)

            info = run_observed(
                ["dotnet", str(assembly), "-assemblyInfo"], repo,
                iteration_root / "assembly-info.stdout.txt", iteration_root / "assembly-info.stderr.txt", 180, dump_tool,
            )
            require_success("assembly-info", info)

            discovery = run_observed(
                ["dotnet", str(assembly), "-list", "full", "-automated", "-noLogo", "-noColor"], repo,
                iteration_root / "discovery.stdout.txt", iteration_root / "discovery.stderr.txt", 180, dump_tool,
            )
            require_success("discovery", discovery)

            marker = iteration_root / "execution.lifecycle"
            execution = run_observed(
                ["dotnet", str(assembly), "-automated", "-noLogo", "-noColor", "-parallel", "none"],
                repo,
                iteration_root / "execution.stdout.txt",
                iteration_root / "execution.stderr.txt",
                180,
                dump_tool,
                environment={"DOWNKYI_LIFECYCLE_MARKER": str(marker)},
                watch_for_watchdog=True,
                evidence_dir=iteration_root / "exact-watchdog-evidence",
                assembly=ASSEMBLY,
                iteration=iteration,
            )
            if execution.timed_out:
                raise RuntimeError("execution phase timed out")
        except Exception as exc:
            failure = str(exc)
            phase_failures += 1

        watchdog_seen = bool(execution and execution.watchdog_seen)
        capture_succeeded = bool(
            watchdog_seen and execution and execution.capture and execution.capture.dump_succeeded
        )
        watchdogs += int(watchdog_seen)
        captures += int(capture_succeeded)
        append_jsonl(
            jsonl,
            {
                "iteration": iteration,
                "watchdogSeen": watchdog_seen,
                "captureSucceeded": capture_succeeded,
                "executionExitCode": execution.exit_code if execution else None,
                "executionTimedOut": execution.timed_out if execution else None,
                "failure": failure,
                "observedAtUtc": iso(),
            },
        )

        if capture_succeeded:
            shutil.move(str(iteration_root), retained / name)
            print(f"FORENSIC SUCCESS: exact watchdog + Full dump captured in {name}")
            break

        if watchdog_seen and not capture_succeeded:
            shutil.move(str(iteration_root), inconclusive / name)
            write_summary(output, started, iteration, watchdogs, captures, phase_failures, "watchdog-observed-without-dump")
            print(
                "FATAL: exact watchdog was observed but the Full dump was not captured. "
                "Stopping instead of blind repetition.",
                file=sys.stderr,
            )
            return 3

        if failure is not None or (execution is not None and execution.exit_code != 0):
            shutil.move(str(iteration_root), inconclusive / name)
            reason = failure or f"execution exit={execution.exit_code}"
            print(f"INCONCLUSIVE: {name} retained: {reason}")
        else:
            shutil.rmtree(iteration_root)

        if iteration % 25 == 0:
            print(
                f"PROGRESS: iterations={iteration} watchdogs={watchdogs} "
                f"captures={captures} failures={phase_failures}",
                flush=True,
            )

    write_summary(output, started, iteration, watchdogs, captures, phase_failures, None)
    print(
        f"SAMPLER COMPLETE: iterations={iteration} watchdogs={watchdogs} "
        f"captures={captures} failures={phase_failures}"
    )
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dump-tool", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--repo", type=Path)
    parser.add_argument("--duration-minutes", type=int, default=285)
    parser.add_argument("--max-iterations", type=int, default=5000)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    dump_tool = args.dump_tool.resolve()
    if not dump_tool.is_file():
        raise RuntimeError(f"dotnet-dump not found: {dump_tool}")
    if args.self_test:
        return self_test(dump_tool, args.out)
    if args.repo is None:
        raise RuntimeError("--repo is required unless --self-test is used")
    return stress(args.repo, dump_tool, args.out, args.duration_minutes, args.max_iterations)


if __name__ == "__main__":
    raise SystemExit(main())
