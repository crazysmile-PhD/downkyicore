#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from datetime import datetime, timedelta, timezone
from pathlib import Path

from issue_183_exact_watchdog_capture import HISTORICAL_SHA, iso, run_observed

ASSEMBLIES = {"core": "DownKyi.Core.Tests", "desktop": "DownKyi.Desktop.Tests"}
PHASES = {"assembly-info", "discovery"}


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


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


def self_test(dump_tool: Path, output: Path) -> int:
    from issue_183_exact_watchdog_capture import self_test as exact_self_test
    return exact_self_test(dump_tool, output)


def probe(
    repo: Path,
    dump_tool: Path,
    output: Path,
    assembly_key: str,
    phase: str,
    duration_minutes: int,
    max_iterations: int,
) -> int:
    repo = repo.resolve()
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    actual_sha = run_text(["git", "-C", str(repo), "rev-parse", "HEAD"], repo).strip()
    if actual_sha != HISTORICAL_SHA:
        raise RuntimeError(f"Historical SHA mismatch: {actual_sha}")
    if assembly_key not in ASSEMBLIES:
        raise RuntimeError(f"Unknown assembly key: {assembly_key}")
    if phase not in PHASES:
        raise RuntimeError(f"Unknown phase: {phase}")

    assembly_name = ASSEMBLIES[assembly_key]
    assembly = repo / "tests" / assembly_name / "bin" / "Release" / "net10.0" / f"{assembly_name}.dll"
    if not assembly.is_file():
        raise RuntimeError(f"Assembly missing: {assembly}")

    if phase == "assembly-info":
        command = ["dotnet", str(assembly), "-assemblyInfo"]
    else:
        command = ["dotnet", str(assembly), "-list", "full", "-automated", "-noLogo", "-noColor"]

    retained = output / "retained"
    work = output / "work"
    retained.mkdir(parents=True, exist_ok=True)
    work.mkdir(parents=True, exist_ok=True)

    started = datetime.now(timezone.utc)
    deadline = started + timedelta(minutes=duration_minutes)
    iteration = watchdogs = captures = unexpected = 0

    while datetime.now(timezone.utc) < deadline and iteration < max_iterations and captures == 0:
        iteration += 1
        root = work / f"iteration-{iteration:06d}"
        root.mkdir(parents=True)
        result = run_observed(
            command,
            repo,
            root / "stdout.txt",
            root / "stderr.txt",
            60,
            dump_tool,
            watch_for_watchdog=True,
            evidence_dir=root / "exact-watchdog-evidence",
            assembly=f"{assembly_name}:{phase}",
            iteration=iteration,
        )
        watchdog = result.watchdog_seen
        captured = bool(result.capture and result.capture.dump_succeeded)
        watchdogs += int(watchdog)
        captures += int(watchdog and captured)

        expected_output = result.exit_code == 0 and not result.timed_out
        if not expected_output:
            unexpected += 1

        if watchdog or not expected_output:
            shutil.move(str(root), retained / root.name)
        else:
            shutil.rmtree(root)

        if watchdog and not captured:
            write_json(output / "summary.json", {
                "schemaVersion": 1,
                "historicalSha": HISTORICAL_SHA,
                "assembly": assembly_name,
                "phase": phase,
                "iterations": iteration,
                "watchdogExecutions": watchdogs,
                "confirmedCaptures": captures,
                "unexpectedFailures": unexpected,
                "fatal": "watchdog-observed-without-dump",
                "finishedAtUtc": iso(),
            })
            return 3

        if iteration % 100 == 0:
            print(f"PROGRESS assembly={assembly_name} phase={phase} iterations={iteration} watchdogs={watchdogs} captures={captures}", flush=True)

    write_json(output / "summary.json", {
        "schemaVersion": 1,
        "historicalSha": HISTORICAL_SHA,
        "assembly": assembly_name,
        "phase": phase,
        "iterations": iteration,
        "watchdogExecutions": watchdogs,
        "confirmedCaptures": captures,
        "unexpectedFailures": unexpected,
        "fatal": None,
        "finishedAtUtc": iso(),
    })
    print(f"PHASE PROBE COMPLETE assembly={assembly_name} phase={phase} iterations={iteration} watchdogs={watchdogs} captures={captures}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dump-tool", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--repo", type=Path)
    parser.add_argument("--assembly", choices=sorted(ASSEMBLIES))
    parser.add_argument("--phase", choices=sorted(PHASES))
    parser.add_argument("--duration-minutes", type=int, default=60)
    parser.add_argument("--max-iterations", type=int, default=10000)
    args = parser.parse_args()

    dump_tool = args.dump_tool.resolve()
    if not dump_tool.is_file():
        raise RuntimeError(f"dotnet-dump not found: {dump_tool}")
    if args.self_test:
        return self_test(dump_tool, args.out)
    if args.repo is None or args.assembly is None or args.phase is None:
        raise RuntimeError("--repo, --assembly, and --phase are required")
    return probe(args.repo, dump_tool, args.out, args.assembly, args.phase, args.duration_minutes, args.max_iterations)


if __name__ == "__main__":
    raise SystemExit(main())
