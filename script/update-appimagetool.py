#!/usr/bin/env python3
"""Propose the newest numeric stable appimagetool release."""

import json
import os
from pathlib import Path
import re
import sys
import urllib.request


MANIFEST = Path(__file__).resolve().parent / "assets" / "external-assets.json"
RELEASES_URL = "https://api.github.com/repos/AppImage/appimagetool/releases"
ASSET_NAME = "appimagetool-x86_64.AppImage"
STABLE_VERSION = re.compile(r"\d+\.\d+\.\d+")


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def fetch_json(url):
    request = urllib.request.Request(
        url,
        headers={"Accept": "application/vnd.github+json", "User-Agent": "DownKyi-appimagetool-updater"},
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def write_output(updated, version):
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"updated={str(updated).lower()}\nversion={version}\n")


def main():
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    tool = manifest["appimagetool"]
    current_version = tool["version"]
    if not STABLE_VERSION.fullmatch(current_version):
        raise ValueError("The current appimagetool version is not a numeric stable release")

    releases = fetch_json(RELEASES_URL)
    candidates = [
        release
        for release in releases
        if not release["draft"]
        and not release["prerelease"]
        and STABLE_VERSION.fullmatch(release["tag_name"])
    ]
    if not candidates:
        raise RuntimeError("No numeric stable appimagetool release was found")
    release = max(candidates, key=lambda item: version_key(item["tag_name"]))
    next_version = release["tag_name"]
    if version_key(next_version) <= version_key(current_version):
        print(f"appimagetool {current_version} is already current")
        write_output(False, current_version)
        return

    asset = next((item for item in release["assets"] if item["name"] == ASSET_NAME), None)
    if asset is None:
        raise RuntimeError(f"appimagetool {next_version} does not contain {ASSET_NAME}")
    expected_url = (
        f"https://github.com/AppImage/appimagetool/releases/download/{next_version}/{ASSET_NAME}"
    )
    if asset["browser_download_url"] != expected_url:
        raise RuntimeError("The stable appimagetool asset URL is unexpected")
    digest = asset.get("digest", "")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise RuntimeError("The stable appimagetool asset has no valid GitHub SHA-256 digest")
    expected_sha256 = digest.removeprefix("sha256:")

    tool["version"] = next_version
    tool["assets"]["linux-x64"]["sha256"] = expected_sha256
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Proposed appimagetool {current_version} -> {next_version}")
    write_output(True, next_version)


if __name__ == "__main__":
    try:
        main()
    except (KeyError, OSError, ValueError, RuntimeError) as error:
        print(f"appimagetool update failed: {error}", file=sys.stderr)
        sys.exit(1)
