#!/usr/bin/env python3
"""Propose the newest stable CPython feature line."""

import json
import os
from pathlib import Path
import re
import sys
import urllib.request


VERSION_FILE = Path(__file__).resolve().parent.parent / ".python-version"
RELEASE_CYCLE_URL = "https://peps.python.org/api/release-cycle.json"
FEATURE_LINE = re.compile(r"3\.\d+")
STABLE_STATUSES = {"bugfix", "security"}


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def fetch_release_cycles():
    request = urllib.request.Request(
        RELEASE_CYCLE_URL,
        headers={"Accept": "application/json", "User-Agent": "DownKyi-Python-version-updater"},
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def write_output(updated, version):
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"updated={str(updated).lower()}\nversion={version}\n")


def main():
    current_version = VERSION_FILE.read_text(encoding="utf-8").strip()
    if not FEATURE_LINE.fullmatch(current_version):
        raise ValueError(".python-version must contain one CPython feature line such as 3.15")

    cycles = fetch_release_cycles()
    candidates = [
        version
        for version, metadata in cycles.items()
        if FEATURE_LINE.fullmatch(version) and metadata.get("status") in STABLE_STATUSES
    ]
    if not candidates:
        raise RuntimeError("The official PEP API returned no stable CPython feature line")

    next_version = max(candidates, key=version_key)
    if version_key(next_version) <= version_key(current_version):
        print(f"Python {current_version} is already current")
        write_output(False, current_version)
        return

    VERSION_FILE.write_text(f"{next_version}\n", encoding="utf-8")
    print(f"Proposed Python {current_version} -> {next_version}")
    write_output(True, next_version)


if __name__ == "__main__":
    try:
        main()
    except (KeyError, OSError, TypeError, ValueError, RuntimeError) as error:
        print(f"Python version update failed: {error}", file=sys.stderr)
        sys.exit(1)
