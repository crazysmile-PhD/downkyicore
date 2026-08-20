#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import shutil
from datetime import datetime, timedelta, timezone
from pathlib import Path

from issue_183_aria_tls_candidate_probe import ASSEMBLY, HISTORICAL_SHA, append_jsonl, iso, run_candidate, run_text, write_json

ARIA_CLASS = "DownKyi.Core.Tests.AriaClientSecurityTests"
SETTINGS_CLASS = "DownKyi.Core.Tests.SettingsStoreTests"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--dump-tool", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--duration-minutes", type=int, default=110)
    parser.add_argument("--max-iterations", type=int, default=1200)
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
    total_tests = 0

    while datetime.now(timezone.utc) < deadline and iterations < args.max_iterations and captures == 0:
        iterations += 1
        root = work / f"iteration-{iterations:06d}"
        result = run_candidate(
            [
                "dotnet", str(assembly),
                "-automated", "async", "-noLogo", "-noColor", "-parallel", "none",
                "-class", ARIA_CLASS,
                "-class", SETTINGS_CLASS,
            ],
            repo,
            root,
            args.dump_tool.resolve(),
            iterations,
            args.timeout_seconds,
        )

        stdout = root / "stdout.txt"
        if stdout.exists():
            with stdout.open("r", encoding="utf-8", errors="replace") as stream:
                tests_this_iteration = sum(1 for line in stream if '"$type":"test-starting"' in line)
        else:
            tests_this_iteration = 0
        result["testsStarted"] = tests_this_iteration
        total_tests += tests_this_iteration

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

        if iterations % 25 == 0:
            print(
                f"PROGRESS iterations={iterations} tests={total_tests} watchdogs={watchdogs} captures={captures} hangs={hangs}",
                flush=True,
            )

    summary = {
        "schemaVersion": 1,
        "historicalSha": HISTORICAL_SHA,
        "assembly": ASSEMBLY,
        "classes": [ARIA_CLASS, SETTINGS_CLASS],
        "iterations": iterations,
        "testsStarted": total_tests,
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
