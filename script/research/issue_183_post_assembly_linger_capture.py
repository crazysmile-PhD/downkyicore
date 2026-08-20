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
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

WATCHDOG = "Waiting 10 seconds for foreground threads to exit"
ASSEMBLY_FINISHED = "test-assembly-finished"
HISTORICAL_SHA = "0ef53f0b3570dd5b5fe8f6fe6eda52e5f9badeb8"
ASSEMBLY = "DownKyi.Core.Tests"


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso(dt: datetime | None = None) -> str:
    return (dt or utc_now()).isoformat().replace("+00:00", "Z")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def append_jsonl(path: Path, value: object) -> None:
    with path.open("a", encoding="utf-8") as out:
        out.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")


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
        raw = exc.stdout or ""
        text = raw.decode("utf-8", errors="replace") if isinstance(raw, bytes) else raw
        output_path.write_text(text + "\nANALYSIS TIMEOUT\n", encoding="utf-8")
        return -3
    except Exception as exc:
        output_path.write_text(f"tool invocation failed: {exc!r}\n", encoding="utf-8")
        return -1


@dataclass
class DumpCapture:
    reason: str
    dump_succeeded: bool
    dump_size_bytes: int
    dump_exit_code: int
    capture_started_at_utc: str
    capture_finished_at_utc: str
    dump_path: str
    metadata_path: str


@dataclass
class ObservedRun:
    process_id: int
    exit_code: int
    timed_out: bool
    watchdog_seen: bool
    assembly_finished_seen: bool
    linger_capture: DumpCapture | None
    exact_capture: DumpCapture | None
    duration_ms: float


def collect_full_dump(
    process: subprocess.Popen,
    evidence_dir: Path,
    dump_tool: Path,
    *,
    reason: str,
    assembly: str,
    iteration: int,
    trigger_stream: str,
    trigger_line: str,
    trigger_at: datetime,
) -> DumpCapture:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    dump_path = evidence_dir / "process-full.dmp"
    dump_log = evidence_dir / "process-full-dump.log"
    analysis_log = evidence_dir / "sos-analysis.txt"
    metadata_path = evidence_dir / "capture.json"
    started = utc_now()
    alive = process.poll() is None
    metadata = {
        "schemaVersion": 10,
        "reason": reason,
        "assembly": assembly,
        "iteration": iteration,
        "processId": process.pid,
        "triggerStream": trigger_stream,
        "triggerLine": trigger_line,
        "triggerObservedAtUtc": iso(trigger_at),
        "captureStartedAtUtc": iso(started),
        "processAliveAtCaptureStart": alive,
        "dumpSucceeded": False,
    }
    write_json(metadata_path, metadata)

    if alive:
        dump_exit = run_tool_to_file(
            [str(dump_tool), "collect", "--type", "Full", "--process-id", str(process.pid), "--output", str(dump_path)],
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
                str(dump_tool), "analyze", str(dump_path),
                "-c", "clrthreads",
                "-c", "threadpool",
                "-c", "clrstack -all",
                "-c", "dumpheap -type MessageBus -stat",
                "-c", "exit",
            ],
            analysis_log,
            timeout=120,
        )

    finished = utc_now()
    metadata.update(
        {
            "dumpExitCode": dump_exit,
            "dumpSucceeded": succeeded,
            "dumpSizeBytes": dump_size,
            "captureFinishedAtUtc": iso(finished),
        }
    )
    write_json(metadata_path, metadata)
    return DumpCapture(
        reason,
        succeeded,
        dump_size,
        dump_exit,
        iso(started),
        iso(finished),
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
    watch_execution: bool = False,
    linger_ms: int = 500,
    evidence_dir: Path | None = None,
    assembly: str = "self-test",
    iteration: int = 0,
) -> ObservedRun:
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    if environment:
        env.update(environment)
    creationflags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
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
        threading.Thread(target=_reader, args=("stdout", process.stdout, stdout_path, events), daemon=True),
        threading.Thread(target=_reader, args=("stderr", process.stderr, stderr_path, events), daemon=True),
    ]
    for reader in readers:
        reader.start()

    done_streams: set[str] = set()
    deadline = time.monotonic() + timeout_seconds
    timed_out = False
    watchdog_seen = False
    assembly_finished_seen = False
    linger_deadline: float | None = None
    linger_trigger: tuple[str, str, datetime] | None = None
    linger_capture: DumpCapture | None = None
    exact_capture: DumpCapture | None = None

    while process.poll() is None or len(done_streams) < 2 or not events.empty():
        try:
            stream_name, line, observed_at, _ = events.get(timeout=0.005)
            if line is None:
                done_streams.add(stream_name)
                continue
            if watch_execution and not assembly_finished_seen and ASSEMBLY_FINISHED in line:
                assembly_finished_seen = True
                linger_deadline = time.monotonic() + (linger_ms / 1000.0)
                linger_trigger = (stream_name, line, observed_at)
            if watch_execution and not watchdog_seen and WATCHDOG in line:
                watchdog_seen = True
                if linger_capture is None or not linger_capture.dump_succeeded:
                    exact_capture = collect_full_dump(
                        process,
                        (evidence_dir or stdout_path.parent / "evidence") / "exact-watchdog",
                        dump_tool,
                        reason="exact-xunit-foreground-thread-watchdog",
                        assembly=assembly,
                        iteration=iteration,
                        trigger_stream=stream_name,
                        trigger_line=line,
                        trigger_at=observed_at,
                    )
        except queue.Empty:
            pass

        if (
            watch_execution
            and linger_deadline is not None
            and linger_capture is None
            and process.poll() is None
            and time.monotonic() >= linger_deadline
            and linger_trigger is not None
        ):
            stream_name, line, observed_at = linger_trigger
            linger_capture = collect_full_dump(
                process,
                (evidence_dir or stdout_path.parent / "evidence") / "post-assembly-linger",
                dump_tool,
                reason=f"post-test-assembly-finished-process-alive-{linger_ms}ms",
                assembly=assembly,
                iteration=iteration,
                trigger_stream=stream_name,
                trigger_line=line,
                trigger_at=observed_at,
            )

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
    return ObservedRun(
        process.pid,
        process.returncode,
        timed_out,
        watchdog_seen,
        assembly_finished_seen,
        linger_capture,
        exact_capture,
        round(duration_ms, 3),
    )


def require_success(name: str, result: ObservedRun) -> None:
    if result.timed_out or result.exit_code != 0:
        raise RuntimeError(f"{name} failed: exit={result.exit_code} timedOut={result.timed_out}")


def run_text(command: list[str], cwd: Path) -> str:
    completed = subprocess.run(command, cwd=str(cwd), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace", check=False)
    if completed.returncode != 0:
        raise RuntimeError(completed.stderr)
    return completed.stdout


def self_test(dump_tool: Path, output: Path, linger_ms: int) -> int:
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)
    pwsh = shutil.which("pwsh") or shutil.which("pwsh.exe")
    if not pwsh:
        raise RuntimeError("pwsh was not found")
    escaped_watchdog = WATCHDOG.replace("'", "''")
    command = (
        f"Write-Output '{{\"type\":\"{ASSEMBLY_FINISHED}\"}}'; "
        f"Start-Sleep -Milliseconds 1200; Write-Output '{escaped_watchdog}'; Start-Sleep -Seconds 8"
    )
    result = run_observed(
        [pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
        output,
        output / "stdout.txt",
        output / "stderr.txt",
        45,
        dump_tool,
        watch_execution=True,
        linger_ms=linger_ms,
        evidence_dir=output / "evidence",
        assembly="Gate.PostAssemblyLinger",
        iteration=1,
    )
    passed = bool(
        result.assembly_finished_seen
        and result.linger_capture
        and result.linger_capture.dump_succeeded
        and result.watchdog_seen
    )
    write_json(
        output / "self-test.json",
        {
            "schemaVersion": 10,
            "passed": passed,
            "processId": result.process_id,
            "assemblyFinishedSeen": result.assembly_finished_seen,
            "lingerDumpSucceeded": bool(result.linger_capture and result.linger_capture.dump_succeeded),
            "watchdogSeen": result.watchdog_seen,
            "generatedAtUtc": iso(),
        },
    )
    if result.linger_capture:
        dump = Path(result.linger_capture.dump_path)
        if dump.exists():
            dump.unlink()
    print(f"POST-ASSEMBLY CAPTURE SELF-TEST: passed={passed} lingerDump={bool(result.linger_capture and result.linger_capture.dump_succeeded)} watchdog={result.watchdog_seen}")
    return 0 if passed else 2


def stress(repo: Path, dump_tool: Path, output: Path, duration_minutes: int, max_iterations: int, linger_ms: int) -> int:
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
    work = output / "work"
    retained.mkdir(parents=True, exist_ok=True)
    work.mkdir(parents=True, exist_ok=True)
    jsonl = output / "iterations.jsonl"
    started = utc_now()
    deadline = started + timedelta(minutes=duration_minutes)
    iteration = watchdogs = confirmed = linger_only = phase_failures = 0
    fatal: str | None = None

    while utc_now() < deadline and iteration < max_iterations and confirmed == 0:
        iteration += 1
        name = f"iteration-{iteration:06d}"
        root = work / name
        root.mkdir(parents=True)
        execution: ObservedRun | None = None
        failure: str | None = None
        try:
            load = run_observed(["dotnet", str(probe), "--assembly", str(assembly)], repo, root / "load.stdout.txt", root / "load.stderr.txt", 180, dump_tool)
            require_success("load", load)
            info = run_observed(["dotnet", str(assembly), "-assemblyInfo"], repo, root / "assembly-info.stdout.txt", root / "assembly-info.stderr.txt", 180, dump_tool)
            require_success("assembly-info", info)
            discovery = run_observed(["dotnet", str(assembly), "-list", "full", "-automated", "-noLogo", "-noColor"], repo, root / "discovery.stdout.txt", root / "discovery.stderr.txt", 180, dump_tool)
            require_success("discovery", discovery)
            marker = root / "execution.lifecycle"
            execution = run_observed(
                ["dotnet", str(assembly), "-automated", "-noLogo", "-noColor", "-parallel", "none"],
                repo,
                root / "execution.stdout.txt",
                root / "execution.stderr.txt",
                180,
                dump_tool,
                environment={"DOWNKYI_LIFECYCLE_MARKER": str(marker)},
                watch_execution=True,
                linger_ms=linger_ms,
                evidence_dir=root / "evidence",
                assembly=ASSEMBLY,
                iteration=iteration,
            )
            if execution.timed_out:
                raise RuntimeError("execution timed out")
            if execution.watchdog_seen:
                watchdogs += 1
                dump_ok = bool(
                    (execution.linger_capture and execution.linger_capture.dump_succeeded)
                    or (execution.exact_capture and execution.exact_capture.dump_succeeded)
                )
                if not dump_ok:
                    raise RuntimeError("exact watchdog observed without same-execution Full dump")
                confirmed += 1
            elif execution.linger_capture and execution.linger_capture.dump_succeeded:
                linger_only += 1
        except Exception as exc:
            failure = repr(exc)
            phase_failures += 1

        record = {
            "schemaVersion": 10,
            "iteration": iteration,
            "watchdogSeen": bool(execution and execution.watchdog_seen),
            "assemblyFinishedSeen": bool(execution and execution.assembly_finished_seen),
            "lingerDumpSucceeded": bool(execution and execution.linger_capture and execution.linger_capture.dump_succeeded),
            "exactDumpSucceeded": bool(execution and execution.exact_capture and execution.exact_capture.dump_succeeded),
            "executionExitCode": execution.exit_code if execution else None,
            "executionDurationMs": execution.duration_ms if execution else None,
            "failure": failure,
            "finishedAtUtc": iso(),
        }
        append_jsonl(jsonl, record)

        retain = bool(execution and (execution.watchdog_seen or execution.linger_capture or execution.exact_capture)) or failure is not None
        if retain:
            target = retained / name
            if target.exists():
                shutil.rmtree(target)
            shutil.move(str(root), str(target))
        else:
            shutil.rmtree(root, ignore_errors=True)

        if failure and execution and execution.watchdog_seen:
            fatal = failure
            break

    write_json(
        output / "summary.json",
        {
            "schemaVersion": 10,
            "historicalSha": HISTORICAL_SHA,
            "assembly": ASSEMBLY,
            "lingerThresholdMs": linger_ms,
            "iterations": iteration,
            "watchdogExecutions": watchdogs,
            "confirmedCaptures": confirmed,
            "lingerOnlyCaptures": linger_only,
            "phaseFailures": phase_failures,
            "fatal": fatal,
            "startedAtUtc": iso(started),
            "finishedAtUtc": iso(),
        },
    )
    print(f"iterations={iteration} watchdogs={watchdogs} confirmed={confirmed} lingerOnly={linger_only} failures={phase_failures}")
    return 0 if fatal is None else 3


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path)
    parser.add_argument("--dump-tool", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--duration-minutes", type=int, default=285)
    parser.add_argument("--max-iterations", type=int, default=5000)
    parser.add_argument("--linger-ms", type=int, default=500)
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.self_test:
        return self_test(args.dump_tool.resolve(), args.out.resolve(), args.linger_ms)
    if args.repo is None:
        raise SystemExit("--repo is required outside --self-test")
    return stress(args.repo, args.dump_tool.resolve(), args.out, args.duration_minutes, args.max_iterations, args.linger_ms)


if __name__ == "__main__":
    raise SystemExit(main())
