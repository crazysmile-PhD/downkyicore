#!/usr/bin/env python3
"""Build the FFmpeg executables used by one DownKyi runtime identifier."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = Path(__file__).resolve().parent
TRIPLETS = {
    "win-x86": "x86-windows-static-release",
    "win-x64": "x64-windows-static-release",
    "linux-x64": "x64-linux-release",
    "linux-arm64": "arm64-linux-release",
    "osx-x64": "x64-osx-release",
    "osx-arm64": "arm64-osx-release",
}
REQUIRED_ENCODERS = {
    "win-x86": ("h264_nvenc", "h264_qsv", "h264_amf"),
    "win-x64": ("h264_nvenc", "h264_qsv", "h264_amf"),
    "linux-x64": ("h264_nvenc", "h264_qsv", "h264_vaapi"),
    "linux-arm64": ("h264_nvenc", "h264_vaapi"),
    "osx-x64": ("h264_videotoolbox",),
    "osx-arm64": ("h264_videotoolbox",),
}


def run(*args, cwd=None, capture=False):
    return subprocess.run(args, cwd=cwd, check=True, text=True,
                          capture_output=capture)


def ensure_vcpkg(path, baseline):
    changed_baseline = False
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        run("git", "clone", "--filter=blob:none", "--depth=1",
            "https://github.com/microsoft/vcpkg.git", str(path))
    current = run("git", "-C", str(path), "rev-parse", "HEAD", capture=True).stdout.strip()
    if current != baseline:
        run("git", "-C", str(path), "fetch", "--filter=blob:none",
            "--depth=1", "origin", baseline)
        run("git", "-C", str(path), "checkout", "--detach", baseline)
        changed_baseline = True
    executable = path / ("vcpkg.exe" if os.name == "nt" else "vcpkg")
    if changed_baseline or not executable.exists():
        bootstrap = path / ("bootstrap-vcpkg.bat" if os.name == "nt" else "bootstrap-vcpkg.sh")
        if os.name == "nt":
            run("cmd", "/c", str(bootstrap), "-disableMetrics", cwd=path)
        else:
            run("sh", str(bootstrap), "-disableMetrics", cwd=path)
    return executable


def source_identity(vcpkg_root):
    port = vcpkg_root / "ports" / "ffmpeg"
    port_manifest = json.loads((port / "vcpkg.json").read_text(encoding="utf-8"))
    version = port_manifest.get("version") or port_manifest.get("version-string")
    portfile = (port / "portfile.cmake").read_text(encoding="utf-8")
    match = re.search(r"(?m)^\s*SHA512\s+([0-9a-fA-F]{128})\s*$", portfile)
    if not version or not match or "REPO ffmpeg/ffmpeg" not in portfile:
        raise RuntimeError("The pinned vcpkg port no longer identifies the official FFmpeg archive")
    helper = vcpkg_root / "ports" / "ffmpeg-bin2c"
    helper_manifest = json.loads((helper / "vcpkg.json").read_text(encoding="utf-8"))
    helper_portfile = (helper / "portfile.cmake").read_text(encoding="utf-8")
    helper_hash = re.search(r"(?m)^\s*SHA512\s+([0-9a-fA-F]{128})\s*$", helper_portfile)
    if (helper_manifest.get("version") != version or not helper_hash or
            helper_hash.group(1).lower() != match.group(1).lower()):
        raise RuntimeError("FFmpeg and its bin2c build tool require different source archives")
    return version, match.group(1).lower()


def prepare_source_archive(vcpkg_root):
    version, expected_sha512 = source_identity(vcpkg_root)
    archive = MANIFEST / "ffmpeg-source.tar.gz"
    digest = hashlib.sha512()
    with archive.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    if digest.hexdigest().lower() != expected_sha512:
        raise RuntimeError(f"The stored FFmpeg source does not match the pinned {version} port")
    downloads = vcpkg_root / "downloads"
    downloads.mkdir(parents=True, exist_ok=True)
    cached = downloads / f"ffmpeg-ffmpeg-n{version}.tar.gz"
    shutil.copy2(archive, cached)
    return version


def validate(ffmpeg, ffprobe, rid):
    version = run(str(ffmpeg), "-version", capture=True).stdout
    if not version.startswith("ffmpeg version "):
        raise RuntimeError("The source build did not produce a working FFmpeg executable")
    run(str(ffprobe), "-version", capture=True)
    encoders = run(str(ffmpeg), "-hide_banner", "-encoders", capture=True).stdout
    for name in ("libx264", "libmp3lame", "aac", "mjpeg", *REQUIRED_ENCODERS[rid]):
        if not any(parts[1] == name for line in encoders.splitlines()
                   if len(parts := line.split()) >= 2):
            raise RuntimeError(f"{rid}: missing required encoder {name}")
    with tempfile.TemporaryDirectory(prefix="ffmpeg-smoke-") as temporary:
        directory = Path(temporary)
        image = directory / "input.ppm"
        video = directory / "output.mp4"
        image.write_text("P3\n2 2\n255\n255 0 0  0 255 0  0 0 255  255 255 255\n", encoding="ascii")
        run(str(ffmpeg), "-hide_banner", "-loglevel", "error", "-y", "-loop", "1",
            "-framerate", "1", "-i", str(image), "-t", "1", "-c:v", "libx264",
            "-pix_fmt", "yuv420p", str(video), capture=True)
        if "codec_name=h264" not in run(str(ffprobe), "-v", "error", "-show_entries",
                                        "stream=codec_name", "-of", "default=noprint_wrappers=1",
                                        str(video), capture=True).stdout:
            raise RuntimeError(f"{rid}: H.264 encode/probe smoke failed")

        audio = directory / "input.pcm"
        mp3 = directory / "output.mp3"
        audio.write_bytes(bytes(8820))
        run(str(ffmpeg), "-hide_banner", "-loglevel", "error", "-y", "-f", "s16le",
            "-ar", "44100", "-ac", "1", "-i", str(audio), "-c:a", "libmp3lame",
            str(mp3), capture=True)
        if "codec_name=mp3" not in run(str(ffprobe), "-v", "error", "-show_entries",
                                       "stream=codec_name", "-of", "default=noprint_wrappers=1",
                                       str(mp3), capture=True).stdout:
            raise RuntimeError(f"{rid}: MP3 encode/probe smoke failed")
    print(version.splitlines()[0])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rid", choices=TRIPLETS)
    parser.add_argument("--vcpkg-root", type=Path, default=ROOT / ".tools" / "vcpkg")
    args = parser.parse_args()
    baseline = json.loads((MANIFEST / "vcpkg.json").read_text(encoding="utf-8"))["builtin-baseline"]
    vcpkg = ensure_vcpkg(args.vcpkg_root.resolve(), baseline)
    version = prepare_source_archive(vcpkg.parent)
    print(f"Using stored FFmpeg {version} source archive")
    triplet = TRIPLETS[args.rid]
    run(str(vcpkg), "install", "--triplet", triplet,
        f"--overlay-triplets={MANIFEST / 'triplets'}",
        f"--x-feature={args.rid}", "--binarysource=clear", cwd=MANIFEST)

    installed = MANIFEST / "vcpkg_installed" / triplet
    extension = ".exe" if args.rid.startswith("win-") else ""
    tools = installed / "tools" / "ffmpeg"
    ffmpeg = tools / f"ffmpeg{extension}"
    ffprobe = tools / f"ffprobe{extension}"
    if not ffmpeg.is_file() or not ffprobe.is_file():
        raise FileNotFoundError(f"vcpkg did not install FFmpeg tools in {tools}")
    validate(ffmpeg, ffprobe, args.rid)

    destination = ROOT / "DownKyi.Core" / "Binary" / args.rid / "ffmpeg"
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ffmpeg-stage-", dir=destination.parent) as temporary:
        stage = Path(temporary)
        for source in (ffmpeg, ffprobe):
            shutil.copy2(source, stage / source.name)
        for license_file in sorted((installed / "share").glob("*/copyright")):
            shutil.copy2(license_file, stage / f"LICENSE-{license_file.parent.name}.txt")
        if destination.exists():
            shutil.rmtree(destination)
        shutil.move(str(stage), str(destination))
    print(f"Installed source-built FFmpeg in {destination}")


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        if error.stderr:
            print(error.stderr[-4000:], file=sys.stderr)
        print(f"FFmpeg source build failed: {error}", file=sys.stderr)
        sys.exit(1)
    except (OSError, RuntimeError) as error:
        print(f"FFmpeg source build failed: {error}", file=sys.stderr)
        sys.exit(1)
