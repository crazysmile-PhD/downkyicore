#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import shutil
from datetime import datetime, timedelta, timezone
from pathlib import Path

from issue_183_exact_watchdog_capture import (
    HISTORICAL_SHA,
    run_observed,
    run_text,
)

ASSEMBLIES = [
    "DownKyi.Application.Tests",
    "DownKyi.Architecture.Tests",
    "DownKyi.Core.Tests",
    "DownKyi.Desktop.Tests",
    "DownKyi.Domain.Tests",
    "DownKyi.Infrastructure.Tests",
    "DownKyi.Tests",
]


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


def phase_record(name: str, result) -> dict[str, object]:
    return {
        "phase": name,
        "processId": result.process_id,
        "exitCode": result.exit_code,
        "timedOut": result.timed_out,
        "watchdogSeen": result.watchdog_seen,
        "dumpSucceeded": bool(result.capture and result.capture.dump_succeeded),
        "dumpSizeBytes": result.capture.dump_size_bytes if result.capture else 0,
        "durationMs": result.duration_ms,
    }


def completed_test_assembly_with_failures(stdout_path: Path) -> bool:
    """Return true only for a completed xUnit run whose nonzero exit is ordinary test failure."""
    if not stdout_path.is_file():
        return False
    try:
        for raw in reversed(stdout_path.read_text(encoding="utf-8", errors="replace").splitlines()):
            raw = raw.strip()
            if not raw.startswith("{"):
                continue
            try:
                message = json.loads(raw)
            except json.JSONDecodeError:
                continue
            if message.get("$type") != "test-assembly-finished":
                continue
            return int(message.get("TestsFailed", 0)) > 0
    except OSError:
        return False
    return False


def run_probe(repo: Path, dump_tool: Path, output: Path, passes: int, max_minutes: int) -> int:
    repo = repo.resolve()
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    actual_sha = run_text(["git", "-C", str(repo), "rev-parse", "HEAD"], repo).strip()
    if actual_sha != HISTORICAL_SHA:
        raise RuntimeError(f"Historical SHA mismatch: {actual_sha}")

    lifecycle_probe = repo / "tools" / "DownKyi.AssemblyLifecycleProbe" / "bin" / "Release" / "net10.0" / "DownKyi.AssemblyLifecycleProbe.dll"
    if not lifecycle_probe.is_file():
        raise RuntimeError(f"Lifecycle probe missing: {lifecycle_probe}")

    assemblies: dict[str, Path] = {}
    for name in ASSEMBLIES:
        dll = repo / "tests" / name / "bin" / "Release" / "net10.0" / f"{name}.dll"
        if not dll.is_file():
            raise RuntimeError(f"Historical test assembly missing: {dll}")
        assemblies[name] = dll

    started = utc_now()
    deadline = started + timedelta(minutes=max_minutes)
    jsonl = output / "iterations.jsonl"
    work = output / "work"
    retained = output / "retained"
    work.mkdir(parents=True, exist_ok=True)
    retained.mkdir(parents=True, exist_ok=True)

    total_executions = 0
    watchdogs = 0
    captures = 0
    unexpected = 0
    ordinary_test_failures = 0
    completed_passes = 0
    per_assembly = {name: 0 for name in ASSEMBLIES}

    for pass_index in range(1, passes + 1):
        if utc_now() >= deadline:
            break

        for assembly_name in ASSEMBLIES:
            dll = assemblies[assembly_name]
            for iteration in range(1, 51):
                if utc_now() >= deadline:
                    break

                total_executions += 1
                per_assembly[assembly_name] += 1
                token = f"pass-{pass_index:02d}-{assembly_name}-iteration-{iteration:02d}"
                root = work / token
                root.mkdir(parents=True, exist_ok=True)
                phases: list[dict[str, object]] = []
                fatal: str | None = None
                watchdog_phase: str | None = None
                dump_succeeded = False
                ordinary_test_failure = False

                try:
                    load = run_observed(
                        ["dotnet", str(lifecycle_probe), "--assembly", str(dll)],
                        repo,
                        root / "load.stdout.txt",
                        root / "load.stderr.txt",
                        180,
                        dump_tool,
                    )
                    phases.append(phase_record("load", load))
                    if load.timed_out or load.exit_code != 0:
                        fatal = f"load failed exit={load.exit_code} timeout={load.timed_out}"

                    for phase_name, command in [
                        ("assembly-info", ["dotnet", str(dll), "-assemblyInfo"]),
                        ("discovery", ["dotnet", str(dll), "-list", "full", "-automated", "-noLogo", "-noColor"]),
                    ]:
                        if fatal:
                            break
                        result = run_observed(
                            command,
                            repo,
                            root / f"{phase_name}.stdout.txt",
                            root / f"{phase_name}.stderr.txt",
                            180,
                            dump_tool,
                            watch_for_watchdog=True,
                            evidence_dir=root / f"{phase_name}-exact-watchdog",
                            assembly=assembly_name,
                            iteration=iteration,
                        )
                        phases.append(phase_record(phase_name, result))
                        if result.watchdog_seen:
                            watchdogs += 1
                            watchdog_phase = phase_name
                            dump_succeeded = bool(result.capture and result.capture.dump_succeeded)
                            if dump_succeeded:
                                captures += 1
                            break
                        if result.timed_out or result.exit_code != 0:
                            fatal = f"{phase_name} failed exit={result.exit_code} timeout={result.timed_out}"
                            break

                    if not fatal and not watchdog_phase:
                        marker = root / "execution.lifecycle"
                        stdout_path = root / "execution.stdout.txt"
                        execution = run_observed(
                            ["dotnet", str(dll), "-automated", "-noLogo", "-noColor", "-parallel", "none"],
                            repo,
                            stdout_path,
                            root / "execution.stderr.txt",
                            180,
                            dump_tool,
                            environment={"DOWNKYI_LIFECYCLE_MARKER": str(marker)},
                            watch_for_watchdog=True,
                            evidence_dir=root / "execution-exact-watchdog",
                            assembly=assembly_name,
                            iteration=iteration,
                        )
                        phases.append(phase_record("execution", execution))
                        if execution.watchdog_seen:
                            watchdogs += 1
                            watchdog_phase = "execution"
                            dump_succeeded = bool(execution.capture and execution.capture.dump_succeeded)
                            if dump_succeeded:
                                captures += 1
                        elif execution.timed_out:
                            fatal = f"execution timed out exit={execution.exit_code}"
                        elif execution.exit_code != 0:
                            # A fully completed xUnit assembly may legitimately return nonzero because
                            # one test assertion failed. That is orthogonal to this shutdown experiment;
                            # retain it as evidence, record it, and continue the historical host sequence.
                            ordinary_test_failure = completed_test_assembly_with_failures(stdout_path)
                            if ordinary_test_failure:
                                ordinary_test_failures += 1
                            else:
                                fatal = f"execution failed exit={execution.exit_code} timeout=False without completed failing assembly"

                except Exception as exc:
                    fatal = repr(exc)

                record = {
                    "schemaVersion": 8,
                    "pass": pass_index,
                    "assembly": assembly_name,
                    "iteration": iteration,
                    "startedAtUtc": iso(),
                    "watchdogPhase": watchdog_phase,
                    "dumpSucceeded": dump_succeeded,
                    "ordinaryTestFailure": ordinary_test_failure,
                    "fatal": fatal,
                    "phases": phases,
                }
                append_jsonl(jsonl, record)

                if watchdog_phase or fatal or ordinary_test_failure:
                    destination = retained / token
                    if destination.exists():
                        shutil.rmtree(destination)
                    shutil.move(str(root), str(destination))
                else:
                    shutil.rmtree(root, ignore_errors=True)

                if watchdog_phase:
                    summary = {
                        "schemaVersion": 8,
                        "historicalSha": HISTORICAL_SHA,
                        "historicalOrder": ASSEMBLIES,
                        "iterationsPerAssemblyPerPass": 50,
                        "passesRequested": passes,
                        "passesCompleted": completed_passes,
                        "totalExecutions": total_executions,
                        "perAssemblyExecutions": per_assembly,
                        "watchdogExecutions": watchdogs,
                        "confirmedCaptures": captures,
                        "ordinaryTestFailures": ordinary_test_failures,
                        "unexpectedFailures": unexpected,
                        "watchdogAssembly": assembly_name,
                        "watchdogIteration": iteration,
                        "watchdogPhase": watchdog_phase,
                        "dumpSucceeded": dump_succeeded,
                        "startedAtUtc": iso(started),
                        "finishedAtUtc": iso(),
                    }
                    write_json(output / "summary.json", summary)
                    print(json.dumps(summary, indent=2))
                    return 0 if dump_succeeded else 3

                if fatal:
                    unexpected += 1
                    summary = {
                        "schemaVersion": 8,
                        "historicalSha": HISTORICAL_SHA,
                        "historicalOrder": ASSEMBLIES,
                        "iterationsPerAssemblyPerPass": 50,
                        "passesRequested": passes,
                        "passesCompleted": completed_passes,
                        "totalExecutions": total_executions,
                        "perAssemblyExecutions": per_assembly,
                        "watchdogExecutions": watchdogs,
                        "confirmedCaptures": captures,
                        "ordinaryTestFailures": ordinary_test_failures,
                        "unexpectedFailures": unexpected,
                        "fatal": fatal,
                        "startedAtUtc": iso(started),
                        "finishedAtUtc": iso(),
                    }
                    write_json(output / "summary.json", summary)
                    print(json.dumps(summary, indent=2))
                    return 4

            if utc_now() >= deadline:
                break

        if utc_now() >= deadline:
            break
        completed_passes = pass_index

    summary = {
        "schemaVersion": 8,
        "historicalSha": HISTORICAL_SHA,
        "historicalOrder": ASSEMBLIES,
        "iterationsPerAssemblyPerPass": 50,
        "passesRequested": passes,
        "passesCompleted": completed_passes,
        "totalExecutions": total_executions,
        "perAssemblyExecutions": per_assembly,
        "watchdogExecutions": watchdogs,
        "confirmedCaptures": captures,
        "ordinaryTestFailures": ordinary_test_failures,
        "unexpectedFailures": unexpected,
        "startedAtUtc": iso(started),
        "finishedAtUtc": iso(),
    }
    write_json(output / "summary.json", summary)
    print(json.dumps(summary, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--dump-tool", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--passes", type=int, default=3)
    parser.add_argument("--max-minutes", type=int, default=120)
    args = parser.parse_args()
    return run_probe(args.repo, args.dump_tool, args.out, args.passes, args.max_minutes)


if __name__ == "__main__":
    raise SystemExit(main())
