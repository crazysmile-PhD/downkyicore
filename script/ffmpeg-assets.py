#!/usr/bin/env python3
"""Fail-closed FFmpeg asset discovery, validation, mirroring and preflight helpers.

The package downloaders deliberately do not call the GitHub API.  This program
is the separate updater/preflight owner for the FFmpeg section of
``external-assets.json`` and is also the common SHA-256 verifier used by the
package downloaders.
"""

from __future__ import annotations

import argparse
import copy
import datetime as dt
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path
from typing import Any, Iterable


EXPECTED_RIDS = ("win-x86", "win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
MIRROR_OWNER = "crazysmile-PhD"
BTBN_REPOSITORY = "BtbN/FFmpeg-Builds"
YTDLP_REPOSITORY = "yt-dlp/FFmpeg-Builds"
GITHUB_API = "https://api.github.com"
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
NATIVE_RUNNERS_BY_RID = {
    "win-x86": {"runner": "windows-latest", "architecture": "x64"},
    "win-x64": {"runner": "windows-latest", "architecture": "x64"},
    "linux-x64": {"runner": "ubuntu-latest", "architecture": "x64"},
    "linux-arm64": {"runner": "ubuntu-24.04-arm", "architecture": "arm64"},
    "osx-x64": {"runner": "macos-15-intel", "architecture": "x64"},
    "osx-arm64": {"runner": "macos-15", "architecture": "arm64"},
}


class AssetError(RuntimeError):
    """A validation error that must stop the updater or package path."""


def load_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise AssetError(f"Expected a JSON object in {path}.")
    return value


def write_json(path: Path, value: dict[str, Any]) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_file(path: Path, expected: str) -> None:
    expected = expected.lower()
    if not SHA256_RE.fullmatch(expected):
        raise AssetError(f"Invalid SHA-256 value for {path}: {expected!r}.")
    if not path.is_file() or path.stat().st_size == 0:
        raise AssetError(f"Downloaded asset is empty or missing: {path}.")
    actual = sha256_file(path)
    if actual != expected:
        raise AssetError(f"Checksum mismatch for {path}. Expected {expected}, got {actual}.")


def github_api(path: str) -> Any:
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2026-03-10",
        "User-Agent": "downkyicore-ffmpeg-updater",
    }
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(
        f"{GITHUB_API}{path}",
        headers=headers,
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError) as error:
        raise AssetError(f"GitHub API request failed for {path}: {error}.") from error


def release_assets(repository: str) -> list[dict[str, Any]]:
    releases = github_api(f"/repos/{repository}/releases?per_page=30")
    if not isinstance(releases, list):
        raise AssetError(f"GitHub API returned an invalid release list for {repository}.")
    return releases


def digest_from_api(asset: dict[str, Any]) -> str:
    digest = asset.get("digest")
    if not isinstance(digest, str) or not digest.startswith("sha256:"):
        raise AssetError(f"Upstream asset {asset.get('name')!r} has no GitHub SHA-256 digest.")
    value = digest.removeprefix("sha256:").lower()
    if not SHA256_RE.fullmatch(value):
        raise AssetError(f"Upstream asset {asset.get('name')!r} has an invalid SHA-256 digest.")
    return value


def find_release(repository: str, patterns: dict[str, re.Pattern[str]]) -> tuple[dict[str, Any], dict[str, dict[str, Any]]]:
    """Return the newest non-floating release that contains every required asset."""
    for release in release_assets(repository):
        tag = release.get("tag_name")
        if (
            not isinstance(tag, str)
            or tag == "latest"
            or release.get("draft")
            or release.get("prerelease")
            or not tag.startswith("autobuild-")
        ):
            continue
        assets = release.get("assets")
        if not isinstance(assets, list):
            continue
        selected: dict[str, dict[str, Any]] = {}
        for rid, pattern in patterns.items():
            matches = [asset for asset in assets if isinstance(asset.get("name"), str) and pattern.fullmatch(asset["name"])]
            if len(matches) != 1:
                break
            try:
                digest_from_api(matches[0])
            except AssetError:
                break
            selected[rid] = matches[0]
        if len(selected) == len(patterns):
            return release, selected
    expected = ", ".join(patterns)
    raise AssetError(f"No complete fixed upstream release was found for {repository}; required RIDs: {expected}.")


def mirrored_file_name(rid: str, upstream_tag: str, original_name: str, digest: str) -> str:
    suffix = "".join(Path(original_name).suffixes)
    safe_tag = re.sub(r"[^A-Za-z0-9._-]+", "-", upstream_tag).strip("-")
    return f"ffmpeg-{rid}-{safe_tag}-{digest[:16]}{suffix}"


def file_record(role: str, rid: str, repository: str, release_tag: str, asset: dict[str, Any]) -> dict[str, Any]:
    name = asset.get("name")
    url = asset.get("browser_download_url")
    if not isinstance(name, str) or not isinstance(url, str):
        raise AssetError(f"Malformed upstream asset in {repository} release {release_tag}.")
    digest = digest_from_api(asset)
    size = asset.get("size")
    if not isinstance(size, int) or size <= 0:
        raise AssetError(f"Upstream asset {name!r} has an invalid size.")
    return {
        "role": role,
        "sourceUrl": url,
        "sha256": digest,
        "originalAssetName": name,
        "mirroredFileName": mirrored_file_name(rid, release_tag, name, digest),
        "expectedSize": size,
    }


def btbn_candidate() -> dict[str, Any]:
    patterns = {
        "win-x64": re.compile(r"ffmpeg-.+-win64-gpl\.zip"),
        "linux-x64": re.compile(r"ffmpeg-.+-linux64-gpl\.tar\.xz"),
        "linux-arm64": re.compile(r"ffmpeg-.+-linuxarm64-gpl\.tar\.xz"),
    }
    release, assets = find_release(BTBN_REPOSITORY, patterns)
    tag = release["tag_name"]
    result: dict[str, Any] = {}
    for rid, asset in assets.items():
        result[rid] = {
            "upstreamRepository": f"https://github.com/{BTBN_REPOSITORY}",
            "upstreamRelease": tag,
            "ffmpegVersion": asset["name"],
            "files": [file_record("ffmpeg", rid, BTBN_REPOSITORY, tag, asset)],
        }
    return {"version": f"btbn-{tag}", "assets": result}


def bootstrap_candidate(manifest: dict[str, Any]) -> dict[str, Any]:
    """Build the first all-RID mirror candidate without scraping HTML.

    BtbN and yt-dlp use their release APIs.  martin-riedl has no release API,
    so its already pinned, reviewed source URLs are only *copied* into the
    first mirror; normal scheduled updates never query or scrape that site.
    """
    candidate = btbn_candidate()
    patterns = {"win-x86": re.compile(r"ffmpeg-.+-win32-gpl\.zip")}
    release, assets = find_release(YTDLP_REPOSITORY, patterns)
    asset = assets["win-x86"]
    candidate["assets"]["win-x86"] = {
        "upstreamRepository": f"https://github.com/{YTDLP_REPOSITORY}",
        "upstreamRelease": release["tag_name"],
        "ffmpegVersion": asset["name"],
        "files": [file_record("ffmpeg", "win-x86", YTDLP_REPOSITORY, release["tag_name"], asset)],
    }

    source_assets = manifest.get("ffmpeg", {}).get("assets", {})
    for rid in ("osx-x64", "osx-arm64"):
        source = source_assets.get(rid)
        if not isinstance(source, dict):
            raise AssetError(f"Bootstrap requires an existing pinned source entry for {rid}.")
        url = source.get("url")
        digest = source.get("sha256")
        if not isinstance(url, str) or not isinstance(digest, str) or not SHA256_RE.fullmatch(digest.lower()):
            raise AssetError(f"Bootstrap source for {rid} is missing URL or SHA-256.")
        source_name = Path(urllib.parse.urlparse(url).path).name
        files = [{
            "role": "ffmpeg",
            "sourceUrl": url,
            "sha256": digest.lower(),
            "originalAssetName": source_name,
            "mirroredFileName": mirrored_file_name(rid, "martin-riedl-pinned", source_name, digest.lower()),
        }]
        probe_url = source.get("ffprobeUrl")
        probe_digest = source.get("ffprobeSha256")
        if isinstance(probe_url, str) and isinstance(probe_digest, str) and SHA256_RE.fullmatch(probe_digest.lower()):
            probe_name = Path(urllib.parse.urlparse(probe_url).path).name
            files.append({
                "role": "ffprobe",
                "sourceUrl": probe_url,
                "sha256": probe_digest.lower(),
                "originalAssetName": probe_name,
                "mirroredFileName": mirrored_file_name(rid, "martin-riedl-pinned", probe_name, probe_digest.lower()),
            })
        candidate["assets"][rid] = {
            "upstreamRepository": "https://ffmpeg.martin-riedl.de",
            "upstreamRelease": "pinned-source-import",
            "ffmpegVersion": source_name,
            "files": files,
        }
    candidate["version"] = f"{candidate['version']}_ytdlp-{release['tag_name']}_martin-riedl-pinned"
    return candidate


def candidate_is_current(manifest: dict[str, Any], candidate: dict[str, Any]) -> bool:
    current = manifest.get("ffmpeg", {}).get("assets", {})
    for rid, next_asset in candidate["assets"].items():
        existing = current.get(rid)
        if not isinstance(existing, dict):
            return False
        provenance = existing.get("provenance")
        if not isinstance(provenance, dict) or provenance.get("upstreamRelease") != next_asset["upstreamRelease"]:
            return False
        next_files = next_asset["files"]
        primary = next(file for file in next_files if file["role"] == "ffmpeg")
        if existing.get("sha256") != primary["sha256"]:
            return False
    return True


def mirror_url(repository: str, tag: str, file_name: str) -> str:
    return f"https://github.com/{repository}/releases/download/{tag}/{file_name}"


def candidate_tag(candidate: dict[str, Any]) -> str:
    safe = re.sub(r"[^A-Za-z0-9._-]+", "-", candidate["version"]).strip("-")
    return f"ffmpeg-{safe}"


def candidate_matrix(candidate: dict[str, Any]) -> list[dict[str, str]]:
    matrix: list[dict[str, str]] = []
    for rid in candidate["assets"]:
        runner = NATIVE_RUNNERS_BY_RID.get(rid)
        if runner is None:
            raise AssetError(f"Candidate has no native GitHub Actions runner mapping for {rid}.")
        matrix.append({
            "rid": rid,
            "runner": runner["runner"],
            "runnerArchitecture": runner["architecture"],
            "requiredEncoder": "h264_nvenc" if rid.startswith(("win-", "linux-")) else "",
        })
    return matrix


def download(url: str, destination: Path) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": "downkyicore-ffmpeg-updater"})
    try:
        with urllib.request.urlopen(request, timeout=90) as response, destination.open("wb") as stream:
            if getattr(response, "status", 200) < 200 or getattr(response, "status", 200) >= 300:
                raise AssetError(f"Unexpected HTTP status for {url}: {response.status}.")
            shutil.copyfileobj(response, stream)
    except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError) as error:
        raise AssetError(f"Download failed for {url}: {error}.") from error
    if not destination.is_file() or destination.stat().st_size == 0:
        raise AssetError(f"Downloaded zero bytes from {url}.")


def candidate_files(candidate: dict[str, Any], rid: str) -> list[dict[str, Any]]:
    try:
        files = candidate["assets"][rid]["files"]
    except KeyError as error:
        raise AssetError(f"Candidate does not contain {rid}.") from error
    if not isinstance(files, list) or not files:
        raise AssetError(f"Candidate {rid} has no files.")
    return files


def download_candidate(candidate: dict[str, Any], rid: str, destination: Path) -> list[Path]:
    destination.mkdir(parents=True, exist_ok=True)
    paths: list[Path] = []
    for file in candidate_files(candidate, rid):
        path = destination / file["mirroredFileName"]
        download(file["sourceUrl"], path)
        expected_size = file.get("expectedSize")
        if expected_size is not None and path.stat().st_size != expected_size:
            raise AssetError(
                f"Downloaded size mismatch for {path}. Expected {expected_size} bytes, got {path.stat().st_size}."
            )
        verify_file(path, file["sha256"])
        paths.append(path)
    return paths


def record_downloaded_sizes(candidate: dict[str, Any], directory: Path) -> None:
    """Bind every candidate record to the verified bytes that will be uploaded."""
    for rid in candidate["assets"]:
        for file in candidate_files(candidate, rid):
            path = directory / file["mirroredFileName"]
            verify_file(path, file["sha256"])
            actual_size = path.stat().st_size
            expected_size = file.get("expectedSize")
            if expected_size is not None:
                if not isinstance(expected_size, int) or expected_size <= 0:
                    raise AssetError(f"Candidate {rid} has an invalid expected size for {path.name}.")
                if actual_size != expected_size:
                    raise AssetError(
                        f"Downloaded size mismatch for {path}. Expected {expected_size} bytes, got {actual_size}."
                    )
            file["expectedSize"] = actual_size


def find_binary(directory: Path, name: str) -> Path | None:
    matches = [path for path in directory.rglob(name) if path.is_file() and path.stat().st_size > 0]
    if len(matches) != 1:
        return None
    return matches[0]


def extract_archive(archive: Path, destination: Path) -> None:
    try:
        if archive.name.endswith(".zip"):
            with zipfile.ZipFile(archive) as source:
                source.extractall(destination)
        elif archive.name.endswith(".tar.xz"):
            with tarfile.open(archive, mode="r:xz") as source:
                source.extractall(destination, filter="data")
        else:
            raise AssetError(f"Unsupported FFmpeg archive type: {archive.name}.")
    except (OSError, tarfile.TarError, zipfile.BadZipFile) as error:
        raise AssetError(f"Unable to extract {archive}: {error}.") from error


def ensure_executable(path: Path) -> None:
    """Restore executable permission lost by Python zip extraction on Unix."""
    if os.name != "nt":
        path.chmod(path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def validate_candidate_archive(candidate: dict[str, Any], rid: str, directory: Path, execute: bool, required_encoder: str | None) -> None:
    files = candidate_files(candidate, rid)
    primary = next((file for file in files if file["role"] == "ffmpeg"), None)
    if primary is None:
        raise AssetError(f"Candidate {rid} does not contain an ffmpeg archive.")
    with tempfile.TemporaryDirectory(prefix=f"downkyi-ffmpeg-{rid}-") as temp:
        extract_root = Path(temp)
        extract_archive(directory / primary["mirroredFileName"], extract_root)
        binary_name = "ffmpeg.exe" if rid.startswith("win-") else "ffmpeg"
        probe_name = "ffprobe.exe" if rid.startswith("win-") else "ffprobe"
        ffmpeg = find_binary(extract_root, binary_name)
        ffprobe = find_binary(extract_root, probe_name)
        probe_file = next((file for file in files if file["role"] == "ffprobe"), None)
        if ffprobe is None and probe_file is not None:
            probe_root = extract_root / "ffprobe"
            probe_root.mkdir()
            extract_archive(directory / probe_file["mirroredFileName"], probe_root)
            ffprobe = find_binary(probe_root, probe_name)
        if ffmpeg is None:
            raise AssetError(f"Archive layout validation failed for {rid}: ffmpeg is missing or empty.")
        if ffprobe is None:
            raise AssetError(f"Archive layout validation failed for {rid}: ffprobe is missing or empty.")
        if execute:
            for binary in (ffmpeg, ffprobe):
                ensure_executable(binary)
                try:
                    result = subprocess.run([str(binary), "-version"], capture_output=True, text=True, timeout=30, check=False)
                except (OSError, subprocess.TimeoutExpired) as error:
                    raise AssetError(f"Executable validation failed for {rid} {binary.name}: {error}.") from error
                if result.returncode != 0 or not (result.stdout or result.stderr).strip():
                    raise AssetError(f"Executable validation failed for {rid} {binary.name}: exit code {result.returncode}.")
            if required_encoder:
                result = subprocess.run([str(ffmpeg), "-hide_banner", "-encoders"], capture_output=True, text=True, timeout=30, check=False)
                if result.returncode != 0 or required_encoder not in f"{result.stdout}\n{result.stderr}":
                    raise AssetError(f"Capability validation failed for {rid}: encoder {required_encoder} is absent.")


def is_project_owned_url(url: str, owner: str) -> bool:
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != "https" or parsed.netloc.lower() != "github.com":
        return False
    pieces = [piece for piece in parsed.path.split("/") if piece]
    if len(pieces) < 6 or pieces[0].lower() != owner.lower() or pieces[2:4] != ["releases", "download"]:
        return False
    return pieces[4].lower() != "latest" and "/latest/" not in parsed.path.lower()


def is_mirror_url(url: str, repository: str, owner: str) -> bool:
    if not is_project_owned_url(url, owner):
        return False
    pieces = [piece for piece in urllib.parse.urlparse(url).path.split("/") if piece]
    return "/".join(pieces[:2]).lower() == repository.lower()


def validate_manifest(manifest: dict[str, Any], owner: str = MIRROR_OWNER) -> None:
    ffmpeg = manifest.get("ffmpeg")
    if not isinstance(ffmpeg, dict):
        raise AssetError("Manifest is missing the ffmpeg object.")
    required = ffmpeg.get("requiredRids")
    if not isinstance(required, list) or any(not isinstance(rid, str) for rid in required):
        raise AssetError("ffmpeg.requiredRids must be a string array.")
    if len(required) != len(set(required)):
        raise AssetError("ffmpeg.requiredRids contains duplicate RIDs.")
    if set(required) != set(EXPECTED_RIDS):
        raise AssetError("ffmpeg.requiredRids must contain the complete supported FFmpeg RID matrix exactly once.")
    mirror = ffmpeg.get("mirror")
    if not isinstance(mirror, dict) or not isinstance(mirror.get("repository"), str):
        raise AssetError("ffmpeg.mirror.repository is required.")
    if not mirror["repository"].lower().startswith(f"{owner.lower()}/"):
        raise AssetError(f"ffmpeg.mirror.repository must be owned by {owner}.")
    if mirror.get("retention") != "never-delete":
        raise AssetError("ffmpeg.mirror.retention must be never-delete.")
    assets = ffmpeg.get("assets")
    if not isinstance(assets, dict):
        raise AssetError("ffmpeg.assets must be an object.")
    missing = [rid for rid in required if rid not in assets]
    extras = [rid for rid in assets if rid not in required]
    if missing or extras:
        raise AssetError(f"ffmpeg.assets RID mismatch; missing={missing}, unexpected={extras}.")
    for rid in required:
        asset = assets[rid]
        if not isinstance(asset, dict):
            raise AssetError(f"ffmpeg asset {rid} must be an object.")
        url = asset.get("url")
        digest = asset.get("sha256")
        if not isinstance(url, str) or not url:
            raise AssetError(f"ffmpeg asset {rid} is missing url.")
        if not is_mirror_url(url, mirror["repository"], owner):
            raise AssetError(f"ffmpeg asset {rid} must use a fixed project-owned GitHub Release URL, not {url}.")
        if not isinstance(digest, str) or not SHA256_RE.fullmatch(digest.lower()):
            raise AssetError(f"ffmpeg asset {rid} is missing a valid SHA-256.")
        if not isinstance(asset.get("fileName"), str) or not asset["fileName"]:
            raise AssetError(f"ffmpeg asset {rid} is missing fileName.")
        if Path(urllib.parse.urlparse(url).path).name != asset["fileName"]:
            raise AssetError(f"ffmpeg asset {rid} fileName does not match its URL.")
        if rid.startswith("osx-"):
            probe_url = asset.get("ffprobeUrl")
            probe_digest = asset.get("ffprobeSha256")
            probe_name = asset.get("ffprobeFileName")
            if not isinstance(probe_url, str) or not is_mirror_url(probe_url, mirror["repository"], owner):
                raise AssetError(f"ffmpeg asset {rid} is missing a fixed project-owned ffprobe URL.")
            if not isinstance(probe_digest, str) or not SHA256_RE.fullmatch(probe_digest.lower()):
                raise AssetError(f"ffmpeg asset {rid} is missing a valid ffprobe SHA-256.")
            if not isinstance(probe_name, str) or Path(urllib.parse.urlparse(probe_url).path).name != probe_name:
                raise AssetError(f"ffmpeg asset {rid} ffprobeFileName does not match its URL.")
        provenance = asset.get("provenance")
        if not isinstance(provenance, dict):
            raise AssetError(f"ffmpeg asset {rid} is missing provenance.")
        for field in ("upstreamRepository", "upstreamRelease", "originalAssetName", "upstreamUrl", "mirroredAt", "ffmpegVersion"):
            if not isinstance(provenance.get(field), str) or not provenance[field]:
                raise AssetError(f"ffmpeg asset {rid} provenance is missing {field}.")


def expected_file_size(file: dict[str, Any]) -> int:
    size = file.get("expectedSize")
    if not isinstance(size, int) or size <= 0:
        raise AssetError(f"Validated candidate is missing a positive expected size for {file.get('mirroredFileName')!r}.")
    return size


def release_by_tag(repository: str, tag: str) -> dict[str, Any]:
    encoded_tag = urllib.parse.quote(tag, safe="")
    release = github_api(f"/repos/{repository}/releases/tags/{encoded_tag}")
    if not isinstance(release, dict):
        raise AssetError(f"GitHub API returned an invalid mirror release for {repository}@{tag}.")
    return release


def validate_workflow_authority(ref_type: str, ref_name: str, manifest_base: str) -> None:
    """Require the updater checkout and manifest PR base to name one branch."""
    if ref_type != "branch":
        raise AssetError("FFmpeg asset updates must run from a branch ref.")
    if not ref_name or not manifest_base or ref_name != manifest_base:
        raise AssetError(
            f"FFmpeg updater authority mismatch: checkout {ref_name!r}, manifest base {manifest_base!r}."
        )


def collect_mirror_evidence(
    candidate: dict[str, Any], repository: str, tag: str, release: dict[str, Any], timestamp: str,
) -> dict[str, Any]:
    """Create manifest input only from GitHub's read-back release-asset metadata."""
    if not repository.lower().startswith(f"{MIRROR_OWNER.lower()}/"):
        raise AssetError(f"Mirror repository must be controlled by {MIRROR_OWNER}: {repository}.")
    if release.get("tag_name") != tag:
        raise AssetError(f"Mirror release read-back tag mismatch. Expected {tag!r}, got {release.get('tag_name')!r}.")
    if release.get("immutable") is not True:
        raise AssetError(
            f"Mirror release read-back for {repository}@{tag} is not immutable; manifest was not changed."
        )
    release_id = release.get("id")
    if not isinstance(release_id, int) or release_id <= 0:
        raise AssetError(f"Mirror release read-back for {repository}@{tag} has no valid release id.")
    release_assets_value = release.get("assets")
    if not isinstance(release_assets_value, list):
        raise AssetError(f"Mirror release read-back for {repository}@{tag} has no asset list.")

    evidence_assets: dict[str, dict[str, Any]] = {}
    result: dict[str, Any] = {
        "repository": repository,
        "tag": tag,
        "readBack": {"releaseId": release_id, "tag": tag, "immutable": True, "assets": evidence_assets},
        "assets": {},
    }
    for rid, source in candidate["assets"].items():
        files = source["files"]
        files_by_role: dict[str, dict[str, Any]] = {}
        for file in files:
            file_name = file.get("mirroredFileName")
            if not isinstance(file_name, str) or not file_name:
                raise AssetError(f"Validated candidate {rid} has no mirror filename.")
            matches = [asset for asset in release_assets_value if asset.get("name") == file_name]
            if len(matches) != 1:
                raise AssetError(
                    f"Mirror release read-back expected exactly one {file_name!r} asset for {rid}, found {len(matches)}."
                )
            asset = matches[0]
            asset_id = asset.get("id")
            if not isinstance(asset_id, int) or asset_id <= 0 or asset.get("state") != "uploaded":
                raise AssetError(f"Mirror release read-back asset {file_name!r} is not a completed upload.")
            expected_size = expected_file_size(file)
            actual_size = asset.get("size")
            if actual_size != expected_size:
                raise AssetError(
                    f"Mirror release read-back size mismatch for {file_name!r}. "
                    f"Expected {expected_size}, got {actual_size!r}."
                )
            actual_digest = digest_from_api(asset)
            if actual_digest != file["sha256"]:
                raise AssetError(f"Mirror release read-back SHA-256 mismatch for {file_name!r}.")
            actual_url = asset.get("browser_download_url")
            if not isinstance(actual_url, str) or not is_mirror_url(actual_url, repository, MIRROR_OWNER):
                raise AssetError(f"Mirror release read-back URL is not project-owned for {file_name!r}.")
            if Path(urllib.parse.unquote(urllib.parse.urlparse(actual_url).path)).name != file_name:
                raise AssetError(f"Mirror release read-back URL filename mismatch for {file_name!r}.")
            asset_evidence = {
                "releaseAssetId": asset_id,
                "name": file_name,
                "size": actual_size,
                "sha256": actual_digest,
                "url": actual_url,
            }
            evidence_assets[file_name] = asset_evidence
            files_by_role[file["role"]] = asset_evidence

        primary = next((file for file in files if file["role"] == "ffmpeg"), None)
        if primary is None or "ffmpeg" not in files_by_role:
            raise AssetError(f"Validated candidate {rid} has no ffmpeg archive.")
        primary_evidence = files_by_role["ffmpeg"]
        entry: dict[str, Any] = {
            "url": primary_evidence["url"],
            "sha256": primary_evidence["sha256"],
            "fileName": primary_evidence["name"],
            "provenance": {
                "upstreamRepository": source["upstreamRepository"],
                "upstreamRelease": source["upstreamRelease"],
                "originalAssetName": primary["originalAssetName"],
                "upstreamUrl": primary["sourceUrl"],
                "mirroredAt": timestamp,
                "ffmpegVersion": source["ffmpegVersion"],
            },
        }
        probe = next((file for file in files if file["role"] == "ffprobe"), None)
        if probe is not None:
            probe_evidence = files_by_role.get("ffprobe")
            if probe_evidence is None:
                raise AssetError(f"Mirror release read-back evidence is missing ffprobe for {rid}.")
            entry["ffprobeUrl"] = probe_evidence["url"]
            entry["ffprobeSha256"] = probe_evidence["sha256"]
            entry["ffprobeFileName"] = probe_evidence["name"]
            entry["provenance"]["ffprobeOriginalAssetName"] = probe["originalAssetName"]
            entry["provenance"]["ffprobeUpstreamUrl"] = probe["sourceUrl"]
        result["assets"][rid] = entry
    return result


def read_mirror_evidence(candidate: dict[str, Any], repository: str, tag: str, timestamp: str) -> dict[str, Any]:
    return collect_mirror_evidence(candidate, repository, tag, release_by_tag(repository, tag), timestamp)


def validate_mirror_evidence(candidate: dict[str, Any], mirror: dict[str, Any]) -> None:
    if not isinstance(mirror.get("repository"), str) or not isinstance(mirror.get("tag"), str):
        raise AssetError("Mirror result is malformed.")
    read_back = mirror.get("readBack")
    if not isinstance(read_back, dict) or not isinstance(read_back.get("assets"), dict):
        raise AssetError("Mirror release read-back evidence is missing; manifest was not changed.")
    if read_back.get("tag") != mirror["tag"]:
        raise AssetError("Mirror release read-back tag does not match the mirror result; manifest was not changed.")
    if read_back.get("immutable") is not True:
        raise AssetError("Mirror release read-back is not immutable; manifest was not changed.")
    if not isinstance(read_back.get("releaseId"), int) or read_back["releaseId"] <= 0:
        raise AssetError("Mirror release read-back release id is missing; manifest was not changed.")
    for rid, source in candidate["assets"].items():
        for file in source["files"]:
            file_name = file["mirroredFileName"]
            evidence = read_back["assets"].get(file_name)
            if not isinstance(evidence, dict):
                raise AssetError(f"Mirror release read-back evidence is missing for {file_name!r}; manifest was not changed.")
            if evidence.get("name") != file_name:
                raise AssetError(f"Mirror release read-back filename mismatch for {file_name!r}; manifest was not changed.")
            if evidence.get("size") != expected_file_size(file):
                raise AssetError(f"Mirror release read-back size mismatch for {file_name!r}; manifest was not changed.")
            if evidence.get("sha256") != file["sha256"]:
                raise AssetError(f"Mirror release read-back SHA-256 mismatch for {file_name!r}; manifest was not changed.")
            if not isinstance(evidence.get("releaseAssetId"), int) or evidence["releaseAssetId"] <= 0:
                raise AssetError(f"Mirror release read-back asset id is missing for {file_name!r}; manifest was not changed.")
            if not isinstance(evidence.get("url"), str) or not is_mirror_url(
                evidence["url"], mirror["repository"], MIRROR_OWNER,
            ):
                raise AssetError(f"Mirror release read-back URL is invalid for {file_name!r}; manifest was not changed.")


def apply_update(manifest: dict[str, Any], candidate: dict[str, Any], mirror: dict[str, Any]) -> dict[str, Any]:
    """Return a new manifest only after every selected asset has mirror evidence."""
    validate_mirror_evidence(candidate, mirror)
    if not isinstance(mirror.get("assets"), dict):
        raise AssetError("Mirror result is malformed.")
    updated = copy.deepcopy(manifest)
    ffmpeg = updated.setdefault("ffmpeg", {})
    ffmpeg["version"] = candidate["version"]
    ffmpeg["requiredRids"] = list(EXPECTED_RIDS)
    ffmpeg["mirror"] = {
        "repository": mirror["repository"],
        "retention": "never-delete",
        "tagPrefix": "ffmpeg-",
    }
    assets = ffmpeg.setdefault("assets", {})
    for rid in candidate["assets"]:
        entry = mirror["assets"].get(rid)
        if not isinstance(entry, dict):
            raise AssetError(f"Mirror release read-back entry is missing for {rid}; manifest was not changed.")
        expected = next(file for file in candidate["assets"][rid]["files"] if file["role"] == "ffmpeg")
        evidence = mirror["readBack"]["assets"][expected["mirroredFileName"]]
        if (
            entry.get("sha256") != expected["sha256"]
            or entry.get("fileName") != expected["mirroredFileName"]
            or entry.get("url") != evidence["url"]
        ):
            raise AssetError(f"Mirror release read-back entry does not match the validated candidate for {rid}; manifest was not changed.")
        assets[rid] = copy.deepcopy(entry)
    validate_manifest(updated)
    return updated


def http_status(url: str, timeout: int) -> tuple[int, int | None]:
    headers = {"User-Agent": "downkyicore-external-asset-preflight"}
    request = urllib.request.Request(url, method="HEAD", headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            length = response.headers.get("Content-Length")
            return response.status, int(length) if length and length.isdigit() else None
    except urllib.error.HTTPError as error:
        if error.code not in (405, 501):
            return error.code, None
    request = urllib.request.Request(url, headers={**headers, "Range": "bytes=0-0"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            length = response.headers.get("Content-Length")
            return response.status, int(length) if length and length.isdigit() else None
    except urllib.error.HTTPError as error:
        return error.code, None
    except (urllib.error.URLError, TimeoutError):
        return 0, None


def preflight(manifest: dict[str, Any], timeout: int) -> None:
    validate_manifest(manifest)
    ffmpeg = manifest["ffmpeg"]
    failures: list[str] = []
    for rid in ffmpeg["requiredRids"]:
        asset = ffmpeg["assets"][rid]
        checks = [("ffmpeg", asset["url"])]
        if "ffprobeUrl" in asset:
            checks.append(("ffprobe", asset["ffprobeUrl"]))
        for tool, url in checks:
            status, length = http_status(url, timeout)
            if status < 200 or status >= 400 or length == 0:
                failures.append(
                    f"External asset unavailable:\n"
                    f"tool={tool}\n"
                    f"rid={rid}\n"
                    f"version={ffmpeg['version']}\n"
                    f"url={url}\n"
                    f"httpStatus={status}"
                )
    if failures:
        raise AssetError("\n\n".join(failures))


def command_discover(args: argparse.Namespace) -> None:
    manifest = load_json(Path(args.manifest))
    candidate = bootstrap_candidate(manifest) if args.bootstrap else btbn_candidate()
    candidate["noOp"] = False if args.bootstrap else candidate_is_current(manifest, candidate)
    candidate["mirrorTag"] = candidate_tag(candidate)
    write_json(Path(args.output), candidate)


def command_download_candidate(args: argparse.Namespace) -> None:
    candidate = load_json(Path(args.candidates))
    download_candidate(candidate, args.rid, Path(args.destination))


def command_validate_candidate(args: argparse.Namespace) -> None:
    candidate = load_json(Path(args.candidates))
    validate_candidate_archive(candidate, args.rid, Path(args.directory), args.execute, args.required_encoder or None)


def command_record_downloaded_sizes(args: argparse.Namespace) -> None:
    candidate = load_json(Path(args.candidates))
    record_downloaded_sizes(candidate, Path(args.directory))
    write_json(Path(args.candidates), candidate)


def command_read_mirror_evidence(args: argparse.Namespace) -> None:
    candidate = load_json(Path(args.candidates))
    timestamp = args.timestamp or dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    write_json(Path(args.output), read_mirror_evidence(candidate, args.repository, args.tag, timestamp))


def command_apply_update(args: argparse.Namespace) -> None:
    path = Path(args.manifest)
    manifest = load_json(path)
    candidate = load_json(Path(args.candidates))
    mirror = load_json(Path(args.mirror))
    updated = apply_update(manifest, candidate, mirror)
    write_json(path, updated)


def command_matrix(args: argparse.Namespace) -> None:
    candidate = load_json(Path(args.candidates))
    print(json.dumps({"include": candidate_matrix(candidate)}, separators=(",", ":")))


def command_workflow_output(args: argparse.Namespace) -> None:
    candidate = load_json(Path(args.candidates))
    no_op = candidate.get("noOp")
    mirror_tag = candidate.get("mirrorTag")
    if not isinstance(no_op, bool) or not isinstance(mirror_tag, str) or not mirror_tag:
        raise AssetError("Candidate is missing workflow output metadata.")
    print(f"no_op={str(no_op).lower()}")
    print(f"matrix={json.dumps({'include': candidate_matrix(candidate)}, separators=(',', ':'))}")
    print(f"mirror_tag={mirror_tag}")


def command_pr_body(args: argparse.Namespace) -> None:
    candidate = load_json(Path(args.candidates))
    mirror = load_json(Path(args.mirror))
    lines = [
        f"Updates mirrored FFmpeg to `{candidate['version']}`.",
        "",
        f"- Previous version: `{args.previous_version}`",
        f"- New version: `{candidate['version']}`",
        f"- Mirror release: `{mirror['repository']}@{mirror['tag']}`",
        "- Validation: archive layout, ffmpeg/ffprobe execution, SHA-256, and required encoder checks on native runners.",
        "- Historical mirror releases are retained; this PR only points the manifest at a new fixed tag.",
        "",
        "| RID | Upstream release | SHA-256 |",
        "| --- | --- | --- |",
    ]
    for rid, asset in candidate["assets"].items():
        primary = next(file for file in asset["files"] if file["role"] == "ffmpeg")
        lines.append(f"| {rid} | {asset['upstreamRelease']} | `{primary['sha256']}` |")
    Path(args.output).write_text("\n".join(lines) + "\n", encoding="utf-8")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    verify = subparsers.add_parser("verify-file")
    verify.add_argument("--path", required=True)
    verify.add_argument("--sha256", required=True)
    validate = subparsers.add_parser("validate-manifest")
    validate.add_argument("--manifest", required=True)
    preflight_parser = subparsers.add_parser("preflight")
    preflight_parser.add_argument("--manifest", required=True)
    preflight_parser.add_argument("--timeout", type=int, default=30)

    authority = subparsers.add_parser("validate-workflow-authority")
    authority.add_argument("--ref-type", required=True)
    authority.add_argument("--ref-name", required=True)
    authority.add_argument("--manifest-base", required=True)
    discover = subparsers.add_parser("discover")
    discover.add_argument("--manifest", required=True)
    discover.add_argument("--output", required=True)
    discover.add_argument("--bootstrap", action="store_true")
    downloader = subparsers.add_parser("download-candidate")
    downloader.add_argument("--candidates", required=True)
    downloader.add_argument("--rid", required=True)
    downloader.add_argument("--destination", required=True)
    candidate_validator = subparsers.add_parser("validate-candidate")
    candidate_validator.add_argument("--candidates", required=True)
    candidate_validator.add_argument("--rid", required=True)
    candidate_validator.add_argument("--directory", required=True)
    candidate_validator.add_argument("--execute", action="store_true")
    candidate_validator.add_argument("--required-encoder")
    downloaded_sizes = subparsers.add_parser("record-downloaded-sizes")
    downloaded_sizes.add_argument("--candidates", required=True)
    downloaded_sizes.add_argument("--directory", required=True)
    evidence = subparsers.add_parser("read-mirror-evidence")
    evidence.add_argument("--candidates", required=True)
    evidence.add_argument("--repository", required=True)
    evidence.add_argument("--tag", required=True)
    evidence.add_argument("--output", required=True)
    evidence.add_argument("--timestamp")
    updater = subparsers.add_parser("apply-update")
    updater.add_argument("--manifest", required=True)
    updater.add_argument("--candidates", required=True)
    updater.add_argument("--mirror", required=True)
    matrix = subparsers.add_parser("matrix")
    matrix.add_argument("--candidates", required=True)
    workflow_output = subparsers.add_parser("workflow-output")
    workflow_output.add_argument("--candidates", required=True)
    body = subparsers.add_parser("pr-body")
    body.add_argument("--candidates", required=True)
    body.add_argument("--mirror", required=True)
    body.add_argument("--previous-version", required=True)
    body.add_argument("--output", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        if args.command == "verify-file":
            verify_file(Path(args.path), args.sha256)
        elif args.command == "validate-manifest":
            validate_manifest(load_json(Path(args.manifest)))
        elif args.command == "preflight":
            preflight(load_json(Path(args.manifest)), args.timeout)
        elif args.command == "validate-workflow-authority":
            validate_workflow_authority(args.ref_type, args.ref_name, args.manifest_base)
        elif args.command == "discover":
            command_discover(args)
        elif args.command == "download-candidate":
            command_download_candidate(args)
        elif args.command == "validate-candidate":
            command_validate_candidate(args)
        elif args.command == "record-downloaded-sizes":
            command_record_downloaded_sizes(args)
        elif args.command == "read-mirror-evidence":
            command_read_mirror_evidence(args)
        elif args.command == "apply-update":
            command_apply_update(args)
        elif args.command == "matrix":
            command_matrix(args)
        elif args.command == "workflow-output":
            command_workflow_output(args)
        elif args.command == "pr-body":
            command_pr_body(args)
        else:
            raise AssetError(f"Unsupported command: {args.command}.")
    except AssetError as error:
        print(f"FFmpeg asset validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
