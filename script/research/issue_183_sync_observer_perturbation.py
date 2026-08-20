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

import issue_183_exact_watchdog_capture as exact

ASSEMBLIES = ("DownKyi.Core.Tests", "DownKyi.Desktop.Tests")


def iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def append_jsonl(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as out:
        out.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")


def run_execution(command: list[str], cwd: Path, stdout_path: Path, stderr_path: Path,
                  environment: dict[str, str], dump_tool: Path, stack_tool: Path,
                  evidence_dir: Path, assembly: str, iteration: int,
                  timeout_seconds: int = 180, perturb_at_seconds: float = 4.0) -> dict:
    env = os.environ.copy()
    env.update(environment)
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    creationflags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    started_ns = time.perf_counter_ns()
    process = subprocess.Popen(
        command, cwd=str(cwd), env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", errors="replace", bufsize=1, creationflags=creationflags)
    assert process.stdout is not None and process.stderr is not None

    events: queue.Queue = queue.Queue()
    readers = [
        threading.Thread(target=exact._reader, args=("stdout", process.stdout, stdout_path, events), daemon=True),
        threading.Thread(target=exact._reader, args=("stderr", process.stderr, stderr_path, events), daemon=True),
    ]
    for thread in readers:
        thread.start()

    watchdog_seen = threading.Event()
    capture_done = threading.Event()
    captures: list[exact.CaptureResult] = []
    capture_lock = threading.Lock()

    def exact_observer() -> None:
        done_streams: set[str] = set()
        while process.poll() is None or len(done_streams) < 2 or not events.empty():
            try:
                stream_name, line, observed_at, observed_perf_ns = events.get(timeout=0.005)
            except queue.Empty:
                continue
            if line is None:
                done_streams.add(stream_name)
                continue
            if exact.WATCHDOG in line and not watchdog_seen.is_set():
                watchdog_seen.set()
                with capture_lock:
                    if not captures:
                        captures.append(exact.capture_exact_signal(
                            process, evidence_dir, dump_tool, assembly, iteration,
                            stream_name, line, observed_at, observed_perf_ns))
                        capture_done.set()

    observer = threading.Thread(target=exact_observer, name="exact-watchdog-observer", daemon=True)
    observer.start()

    deadline = time.monotonic() + timeout_seconds
    perturb_deadline = time.monotonic() + perturb_at_seconds
    perturb_attempted = False
    perturb_exit = None
    perturb_duration_ms = None
    timed_out = False

    # Historical perturbation: deliberately block the controlling observation loop in
    # synchronous dotnet-stack at threshold(5s)-lead(1s)=~4s. The exact watchdog
    # observer above stays independent and continuously draining, so a hit cannot be missed.
    while process.poll() is None:
        now = time.monotonic()
        if not perturb_attempted and now >= perturb_deadline:
            perturb_attempted = True
            evidence_dir.mkdir(parents=True, exist_ok=True)
            started = time.perf_counter_ns()
            perturb_exit = exact.run_tool_to_file(
                [str(stack_tool), "report", "--process-id", str(process.pid)],
                evidence_dir / "historical-synchronous-slow-phase-stack.txt",
                timeout=15)
            perturb_duration_ms = (time.perf_counter_ns() - started) / 1_000_000.0
            write_json(evidence_dir / "historical-synchronous-perturbation.json", {
                "schemaVersion": 1, "processId": process.pid, "assembly": assembly,
                "iteration": iteration, "thresholdSeconds": perturb_at_seconds,
                "stackExitCode": perturb_exit, "durationMs": round(perturb_duration_ms, 3),
                "completedAtUtc": iso(),
            })
        if now >= deadline:
            timed_out = True
            process.kill()
            break
        time.sleep(0.025)

    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        timed_out = True
        process.kill()
        process.wait()
    for thread in readers:
        thread.join(timeout=2)
    observer.join(timeout=15)

    capture = captures[0] if captures else None
    return {
        "processId": process.pid,
        "exitCode": process.returncode,
        "timedOut": timed_out,
        "watchdogSeen": watchdog_seen.is_set(),
        "capture": capture,
        "captureFinished": capture_done.is_set(),
        "perturbAttempted": perturb_attempted,
        "perturbExitCode": perturb_exit,
        "perturbDurationMs": round(perturb_duration_ms, 3) if perturb_duration_ms is not None else None,
        "durationMs": round((time.perf_counter_ns() - started_ns) / 1_000_000.0, 3),
    }


def stress(repo: Path, dump_tool: Path, stack_tool: Path, output: Path,
           duration_minutes: int, iterations_per_batch: int, max_batches: int) -> int:
    repo, output = repo.resolve(), output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if exact.run_text(["git", "-C", str(repo), "rev-parse", "HEAD"], repo).strip() != exact.HISTORICAL_SHA:
        raise RuntimeError("Historical SHA mismatch")
    probe = repo / "tools/DownKyi.AssemblyLifecycleProbe/bin/Release/net10.0/DownKyi.AssemblyLifecycleProbe.dll"
    assemblies = {name: repo / f"tests/{name}/bin/Release/net10.0/{name}.dll" for name in ASSEMBLIES}
    if not probe.is_file() or not all(path.is_file() for path in assemblies.values()):
        raise RuntimeError("Historical target binaries are missing")

    deadline = datetime.now(timezone.utc) + timedelta(minutes=duration_minutes)
    total = watchdogs = confirmed = failures = perturbations = 0
    by_assembly = {name: 0 for name in ASSEMBLIES}
    batch = 0
    jsonl = output / "iterations.jsonl"

    while datetime.now(timezone.utc) < deadline and batch < max_batches and confirmed == 0:
        batch += 1
        for assembly_name in ASSEMBLIES:
            assembly = assemblies[assembly_name]
            for local_iteration in range(1, iterations_per_batch + 1):
                if datetime.now(timezone.utc) >= deadline or confirmed:
                    break
                total += 1
                by_assembly[assembly_name] += 1
                root = output / "work" / f"batch-{batch:04d}" / assembly_name / f"iteration-{local_iteration:04d}"
                root.mkdir(parents=True, exist_ok=True)
                failure = None
                result = None
                try:
                    for name, command in (
                        ("load", ["dotnet", str(probe), "--assembly", str(assembly)]),
                        ("assembly-info", ["dotnet", str(assembly), "-assemblyInfo"]),
                        ("discovery", ["dotnet", str(assembly), "-list", "full", "-automated", "-noLogo", "-noColor"]),
                    ):
                        phase = exact.run_observed(
                            command, repo, root / f"{name}.stdout.txt", root / f"{name}.stderr.txt",
                            180, dump_tool)
                        exact.require_success(name, phase)
                    marker = root / "execution.lifecycle"
                    result = run_execution(
                        ["dotnet", str(assembly), "-automated", "-noLogo", "-noColor", "-parallel", "none"],
                        repo, root / "execution.stdout.txt", root / "execution.stderr.txt",
                        {"DOWNKYI_LIFECYCLE_MARKER": str(marker)}, dump_tool, stack_tool,
                        root / "evidence", assembly_name, total)
                    perturbations += int(result["perturbAttempted"])
                    if result["timedOut"]:
                        raise RuntimeError("execution phase timed out")
                except Exception as exc:
                    failure = str(exc)
                    failures += 1

                seen = bool(result and result["watchdogSeen"])
                captured = bool(seen and result and result["capture"] and result["capture"].dump_succeeded)
                watchdogs += int(seen)
                confirmed += int(captured)
                append_jsonl(jsonl, {
                    "batch": batch, "assembly": assembly_name, "iterationInBatch": local_iteration,
                    "globalExecution": total, "watchdogSeen": seen, "captureSucceeded": captured,
                    "exitCode": result["exitCode"] if result else None,
                    "timedOut": result["timedOut"] if result else None,
                    "perturbAttempted": result["perturbAttempted"] if result else None,
                    "perturbExitCode": result["perturbExitCode"] if result else None,
                    "perturbDurationMs": result["perturbDurationMs"] if result else None,
                    "durationMs": result["durationMs"] if result else None,
                    "failure": failure, "observedAtUtc": iso(),
                })

                if seen and not captured:
                    retained = output / "inconclusive" / f"batch-{batch:04d}-{assembly_name}-iteration-{local_iteration:04d}"
                    retained.parent.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(root), retained)
                    write_json(output / "summary.json", {
                        "schemaVersion": 1, "historicalSha": exact.HISTORICAL_SHA,
                        "totalExecutions": total, "byAssembly": by_assembly,
                        "synchronousPerturbations": perturbations, "watchdogExecutions": watchdogs,
                        "confirmedCaptures": confirmed, "phaseFailures": failures, "batches": batch,
                        "fatal": "watchdog-observed-without-same-pid-dump", "finishedAtUtc": iso(),
                    })
                    return 3
                if captured:
                    retained = output / "retained" / f"batch-{batch:04d}-{assembly_name}-iteration-{local_iteration:04d}"
                    retained.parent.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(root), retained)
                    break
                if failure is not None or (result is not None and result["exitCode"] != 0):
                    retained = output / "inconclusive" / f"batch-{batch:04d}-{assembly_name}-iteration-{local_iteration:04d}"
                    retained.parent.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(root), retained)
                else:
                    shutil.rmtree(root)
            if confirmed or datetime.now(timezone.utc) >= deadline:
                break
        print(f"PROGRESS batch={batch} total={total} perturbations={perturbations} watchdogs={watchdogs} captures={confirmed} failures={failures}", flush=True)

    write_json(output / "summary.json", {
        "schemaVersion": 1, "historicalSha": exact.HISTORICAL_SHA, "finishedAtUtc": iso(),
        "totalExecutions": total, "byAssembly": by_assembly, "synchronousPerturbations": perturbations,
        "watchdogExecutions": watchdogs, "confirmedCaptures": confirmed, "phaseFailures": failures,
        "batches": batch, "success": confirmed > 0,
    })
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--dump-tool", required=True, type=Path)
    parser.add_argument("--stack-tool", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--duration-minutes", type=int, default=285)
    parser.add_argument("--iterations-per-batch", type=int, default=25)
    parser.add_argument("--max-batches", type=int, default=200)
    args = parser.parse_args()
    return stress(args.repo, args.dump_tool.resolve(), args.stack_tool.resolve(), args.out,
                  args.duration_minutes, args.iterations_per_batch, args.max_batches)


if __name__ == "__main__":
    raise SystemExit(main())
