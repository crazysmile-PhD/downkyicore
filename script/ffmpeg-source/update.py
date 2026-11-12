#!/usr/bin/env python3
"""Propose the newest FFmpeg source available in the vcpkg registry."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request

from build import MANIFEST, source_identity


VCPKG_URL = "https://github.com/microsoft/vcpkg.git"
RAW_ROOT = "https://raw.githubusercontent.com/microsoft/vcpkg"
SOURCE_ROOT = "https://github.com/ffmpeg/ffmpeg/archive"


def fetch(url, destination):
    request = urllib.request.Request(url, headers={"User-Agent": "DownKyi-FFmpeg-source-updater"})
    with urllib.request.urlopen(request, timeout=60) as response, destination.open("wb") as output:
        shutil.copyfileobj(response, output)


def read_source_identity(commit, directory):
    for port in ("ffmpeg", "ffmpeg-bin2c"):
        port_directory = directory / "ports" / port
        port_directory.mkdir(parents=True, exist_ok=True)
        for name in ("vcpkg.json", "portfile.cmake"):
            fetch(f"{RAW_ROOT}/{commit}/ports/{port}/{name}", port_directory / name)
    return source_identity(directory)


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def write_output(updated, version):
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"updated={str(updated).lower()}\nversion={version}\n")


def main():
    manifest_path = MANIFEST / "vcpkg.json"
    manifest_text = manifest_path.read_text(encoding="utf-8")
    baseline = json.loads(manifest_text)["builtin-baseline"]
    latest = subprocess.run(["git", "ls-remote", VCPKG_URL, "HEAD"], check=True,
                            capture_output=True, text=True).stdout.split()[0]
    if latest == baseline:
        print("The selected vcpkg baseline is already current")
        write_output(False, "")
        return

    with tempfile.TemporaryDirectory(prefix="ffmpeg-source-update-") as temporary:
        directory = Path(temporary)
        current_version, _ = read_source_identity(baseline, directory / "current")
        next_version, expected_hash = read_source_identity(latest, directory / "next")
        if version_key(next_version) <= version_key(current_version):
            print(f"FFmpeg remains at {current_version}; retaining the selected baseline")
            write_output(False, current_version)
            return

        candidate = directory / "ffmpeg-source.tar.gz"
        fetch(f"{SOURCE_ROOT}/n{next_version}.tar.gz", candidate)
        digest = hashlib.sha512()
        with candidate.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        if digest.hexdigest().lower() != expected_hash:
            raise RuntimeError("The candidate FFmpeg source does not match the pinned vcpkg port")
        if manifest_text.count(baseline) != 1:
            raise RuntimeError("The current baseline must occur exactly once in vcpkg.json")
        shutil.copy2(candidate, MANIFEST / "ffmpeg-source.tar.gz")
        manifest_path.write_text(manifest_text.replace(baseline, latest), encoding="utf-8")
        print(f"Proposed FFmpeg {current_version} -> {next_version} from official source")
        write_output(True, next_version)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"FFmpeg source update failed: {error}", file=sys.stderr)
        sys.exit(1)
