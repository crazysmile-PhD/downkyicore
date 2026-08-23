from __future__ import annotations

import copy
import hashlib
import http.server
import importlib.util
import json
import os
import stat
import subprocess
import sys
import tempfile
import threading
import unittest
import zipfile
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "ffmpeg-assets.py"
SPEC = importlib.util.spec_from_file_location("ffmpeg_assets", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
ffmpeg_assets = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = ffmpeg_assets
SPEC.loader.exec_module(ffmpeg_assets)


def digest(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def valid_manifest(version: str = "btbn-autobuild-2026-08-12-13-15") -> dict:
    assets = {}
    for rid in ffmpeg_assets.EXPECTED_RIDS:
        archive_name = f"ffmpeg-{rid}-{digest(rid)[:16]}.zip"
        entry = {
            "url": f"https://github.com/crazysmile-PhD/downkyi-runtime-assets/releases/download/ffmpeg-{version}/{archive_name}",
            "sha256": digest(rid),
            "fileName": archive_name,
            "provenance": {
                "upstreamRepository": "https://github.com/example/source",
                "upstreamRelease": "fixed-release",
                "originalAssetName": f"upstream-{rid}.zip",
                "upstreamUrl": f"https://example.invalid/upstream-{rid}.zip",
                "mirroredAt": "2026-08-13T00:00:00Z",
                "ffmpegVersion": "N-126086",
            },
        }
        if rid.startswith("osx-"):
            probe_name = f"ffprobe-{rid}-{digest(rid + '-probe')[:16]}.zip"
            entry.update({
                "ffprobeUrl": f"https://github.com/crazysmile-PhD/downkyi-runtime-assets/releases/download/ffmpeg-{version}/{probe_name}",
                "ffprobeSha256": digest(rid + "-probe"),
                "ffprobeFileName": probe_name,
            })
        assets[rid] = entry
    return {
        "ffmpeg": {
            "version": version,
            "requiredRids": list(ffmpeg_assets.EXPECTED_RIDS),
            "mirror": {
                "repository": "crazysmile-PhD/downkyi-runtime-assets",
                "retention": "never-delete",
                "tagPrefix": "ffmpeg-",
            },
            "assets": assets,
        }
    }


def candidate_for(rid: str = "win-x64") -> dict:
    source_name = f"ffmpeg-N-126086-{rid}.zip"
    return {
        "version": "btbn-autobuild-2026-08-12-13-15",
        "assets": {
            rid: {
                "upstreamRepository": "https://github.com/BtbN/FFmpeg-Builds",
                "upstreamRelease": "autobuild-2026-08-12-13-15",
                "ffmpegVersion": "N-126086",
                "files": [{
                    "role": "ffmpeg",
                    "sourceUrl": f"https://example.invalid/{source_name}",
                    "sha256": digest(source_name),
                    "originalAssetName": source_name,
                    "mirroredFileName": f"ffmpeg-{rid}-autobuild-2026-08-12-13-15-{digest(source_name)[:16]}.zip",
                    "expectedSize": 128,
                }],
            }
        },
    }


def release_readback_for(candidate: dict, repository: str, tag: str) -> dict:
    assets = []
    asset_id = 100
    for source in candidate["assets"].values():
        for file in source["files"]:
            name = file["mirroredFileName"]
            assets.append({
                "id": asset_id,
                "name": name,
                "state": "uploaded",
                "size": file["expectedSize"],
                "digest": f"sha256:{file['sha256']}",
                "browser_download_url": ffmpeg_assets.mirror_url(repository, tag, name),
            })
            asset_id += 1
    return {"id": 99, "tag_name": tag, "immutable": True, "assets": assets}


class AssetRequestHandler(http.server.BaseHTTPRequestHandler):
    def do_HEAD(self) -> None:  # noqa: N802
        self.send_response(404 if self.path.endswith("missing.zip") else 200)
        self.send_header("Content-Length", "1")
        self.end_headers()

    def do_GET(self) -> None:  # noqa: N802
        self.do_HEAD()
        if not self.path.endswith("missing.zip"):
            self.wfile.write(b"x")

    def log_message(self, _format: str, *_args: object) -> None:
        return


class FfmpegAssetsTests(unittest.TestCase):
    @unittest.skipIf(os.name == "nt", "Unix executable mode is not meaningful on Windows.")
    def test_candidate_binary_restores_execute_permission_after_zip_extraction(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            binary = Path(temp) / "ffmpeg"
            binary.write_bytes(b"binary")
            binary.chmod(stat.S_IRUSR | stat.S_IWUSR)

            ffmpeg_assets.ensure_executable(binary)

            self.assertNotEqual(0, binary.stat().st_mode & stat.S_IXUSR)

    def test_manifest_requires_every_rid_url_and_checksum(self) -> None:
        manifest = valid_manifest()
        ffmpeg_assets.validate_manifest(manifest)
        del manifest["ffmpeg"]["assets"]["linux-x64"]["sha256"]
        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "linux-x64.*SHA-256"):
            ffmpeg_assets.validate_manifest(manifest)

    def test_manifest_rejects_missing_url(self) -> None:
        manifest = valid_manifest()
        del manifest["ffmpeg"]["assets"]["linux-x64"]["url"]
        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "linux-x64.*url"):
            ffmpeg_assets.validate_manifest(manifest)

    def test_manifest_rejects_mutable_latest_url(self) -> None:
        manifest = valid_manifest()
        manifest["ffmpeg"]["assets"]["win-x64"]["url"] = (
            "https://github.com/crazysmile-PhD/downkyi-runtime-assets/releases/download/latest/ffmpeg.zip"
        )
        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "fixed project-owned"):
            ffmpeg_assets.validate_manifest(manifest)

    def test_manifest_rejects_non_project_owned_url(self) -> None:
        manifest = valid_manifest()
        manifest["ffmpeg"]["assets"]["win-x64"]["url"] = (
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-1/ffmpeg.zip"
        )
        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "project-owned"):
            ffmpeg_assets.validate_manifest(manifest)

    def test_manifest_rejects_duplicate_or_missing_rids(self) -> None:
        manifest = valid_manifest()
        manifest["ffmpeg"]["requiredRids"].append("win-x64")
        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "duplicate"):
            ffmpeg_assets.validate_manifest(manifest)
        manifest = valid_manifest()
        manifest["ffmpeg"]["requiredRids"].remove("linux-arm64")
        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "complete supported"):
            ffmpeg_assets.validate_manifest(manifest)

    def test_current_candidate_is_a_no_op(self) -> None:
        manifest = valid_manifest()
        candidate = candidate_for()
        entry = manifest["ffmpeg"]["assets"]["win-x64"]
        entry["sha256"] = candidate["assets"]["win-x64"]["files"][0]["sha256"]
        entry["provenance"]["upstreamRelease"] = candidate["assets"]["win-x64"]["upstreamRelease"]
        self.assertTrue(ffmpeg_assets.candidate_is_current(manifest, candidate))

    def test_discovery_rejects_incomplete_release(self) -> None:
        incomplete = [{
            "tag_name": "autobuild-2026-08-12-13-15",
            "draft": False,
            "prerelease": False,
            "assets": [{
                "name": "ffmpeg-N-126086-win64-gpl.zip",
                "digest": f"sha256:{digest('win')}",
                "browser_download_url": "https://example.invalid/win.zip",
            }],
        }]
        with mock.patch.object(ffmpeg_assets, "release_assets", return_value=incomplete):
            with self.assertRaisesRegex(ffmpeg_assets.AssetError, "No complete fixed upstream release"):
                ffmpeg_assets.find_release("example/source", {
                    "win-x64": __import__("re").compile(r"ffmpeg-.+-win64-gpl\\.zip"),
                    "linux-x64": __import__("re").compile(r"ffmpeg-.+-linux64-gpl\\.tar\\.xz"),
                })

    def test_package_downloader_verifier_rejects_checksum_mismatch(self) -> None:
        self.assertIn("ffmpeg-assets.py", (SCRIPT.parent / "ffmpeg.ps1").read_text(encoding="utf-8"))
        self.assertIn("ffmpeg-assets.py", (SCRIPT.parent / "ffmpeg.sh").read_text(encoding="utf-8"))
        with tempfile.TemporaryDirectory() as temporary:
            asset = Path(temporary) / "asset.bin"
            asset.write_bytes(b"known bytes")
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "verify-file", "--path", str(asset), "--sha256", digest("wrong")],
                capture_output=True,
                text=True,
                check=False,
            )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Checksum mismatch", result.stderr)

    def test_candidate_validation_rejects_missing_ffprobe(self) -> None:
        candidate = candidate_for("linux-x64")
        file = candidate["assets"]["linux-x64"]["files"][0]
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            archive = directory / file["mirroredFileName"]
            with zipfile.ZipFile(archive, "w") as source:
                source.writestr("bin/ffmpeg", b"not an executable")
            with self.assertRaisesRegex(ffmpeg_assets.AssetError, "ffprobe"):
                ffmpeg_assets.validate_candidate_archive(candidate, "linux-x64", directory, False, None)

    def test_workflow_contracts_guard_tooling_and_production_phases(self) -> None:
        workflow_directory = SCRIPT.parents[1] / ".github" / "workflows"
        updater = (workflow_directory / "update-ffmpeg-assets.yml").read_text(encoding="utf-8")
        build = (workflow_directory / "build.yml").read_text(encoding="utf-8")
        gitignore = (SCRIPT.parents[1] / ".gitignore").read_text(encoding="utf-8")
        self.assertIn(
            'commit-message: "build(deps): update mirrored FFmpeg to ${{ needs.discover.outputs.mirror_tag }}"',
            updater,
        )
        self.assertIn(
            'title: "build(deps): update mirrored FFmpeg to ${{ needs.discover.outputs.mirror_tag }}"',
            updater,
        )
        self.assertIn("read-mirror-evidence", updater)
        self.assertIn("An existing release will not be modified", updater)
        self.assertIn("add-paths: script/assets/external-assets.json", updater)
        self.assertIn(".ffmpeg-update/", gitignore.splitlines())
        self.assertNotIn("record-mirror", updater)
        self.assertIn("workflow_call:", updater)
        self.assertIn("validate-workflow-authority", updater)
        self.assertIn("immutable-releases", updater)
        self.assertIn("needs.discover.outputs.manifest_base", updater)
        self.assertIn("update_ffmpeg_assets:", build)
        self.assertIn("uses: ./.github/workflows/update-ffmpeg-assets.yml", build)
        self.assertIn("manifest_base: ${{ github.ref_name }}", build)
        self.assertIn("- release/v1.1.1-integration", build)
        self.assertIn("uses: raven-actions/actionlint@v2", build)
        self.assertNotIn("shellcheck: false", build)
        self.assertNotIn("pyflakes: false", build)
        self.assertIn("fail-on-error: true", build)
        self.assertIn("shellcheck: true", build)
        self.assertIn("pyflakes: true", build)
        self.assertIn("name: FFmpeg tooling / implementation gate", build)
        self.assertIn("- '.github/workflows/build.yml'", build)
        self.assertIn("uses: dorny/paths-filter@v3", build)
        self.assertIn("- 'script/assets/external-assets.json'", build)
        self.assertIn("name: Production manifest preflight", build)
        self.assertIn("- ffmpeg-tooling", build)
        self.assertIn("- detect-production-manifest-change", build)
        self.assertIn("needs.ffmpeg-tooling.result == 'success'", build)
        self.assertIn("always() && !inputs.update_ffmpeg_assets", build)
        self.assertIn("needs.ffmpeg-tooling.result == 'success'", build)
        self.assertIn("github.event_name != 'pull_request'", build)
        self.assertIn("needs.detect-production-manifest-change.outputs.external_assets == 'true'", build)
        self.assertIn("needs: external-assets-preflight", build)

    def test_workflow_authority_requires_same_branch_checkout_and_manifest_base(self) -> None:
        ffmpeg_assets.validate_workflow_authority(
            "branch",
            "release/v1.1.1-integration",
            "release/v1.1.1-integration",
        )
        for ref_type, ref_name, manifest_base in (
            ("tag", "v1.1.1", "main"),
            ("branch", "main", "release/v1.1.1-integration"),
            ("branch", "", "main"),
        ):
            with self.subTest(ref_type=ref_type, ref_name=ref_name, manifest_base=manifest_base):
                with self.assertRaises(ffmpeg_assets.AssetError):
                    ffmpeg_assets.validate_workflow_authority(ref_type, ref_name, manifest_base)

    def test_macos_candidates_use_distinct_native_runners(self) -> None:
        candidate = {"assets": {"osx-x64": {}, "osx-arm64": {}}}
        matrix = {entry["rid"]: entry for entry in ffmpeg_assets.candidate_matrix(candidate)}
        self.assertEqual("macos-15-intel", matrix["osx-x64"]["runner"])
        self.assertEqual("x64", matrix["osx-x64"]["runnerArchitecture"])
        self.assertEqual("macos-15", matrix["osx-arm64"]["runner"])
        self.assertEqual("arm64", matrix["osx-arm64"]["runnerArchitecture"])
        self.assertNotEqual(matrix["osx-x64"]["runner"], matrix["osx-arm64"]["runner"])

    def test_missing_mirror_readback_evidence_leaves_manifest_unchanged(self) -> None:
        manifest = valid_manifest()
        before = copy.deepcopy(manifest)
        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "read-back evidence is missing"):
            ffmpeg_assets.apply_update(manifest, candidate_for(), {
                "repository": "crazysmile-PhD/downkyi-runtime-assets",
                "tag": "ffmpeg-btbn-autobuild-2026-08-12-13-15",
                "assets": {},
            })
        self.assertEqual(before, manifest)

    def test_mutable_mirror_release_cannot_produce_manifest_evidence(self) -> None:
        repository = "crazysmile-PhD/downkyi-runtime-assets"
        tag = "ffmpeg-btbn-autobuild-2026-08-12-13-15"
        candidate = candidate_for()
        release = release_readback_for(candidate, repository, tag)
        release["immutable"] = False

        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "not immutable"):
            ffmpeg_assets.collect_mirror_evidence(
                candidate,
                repository,
                tag,
                release,
                "2026-08-13T00:00:00Z",
            )

    def test_apply_update_rejects_evidence_that_loses_immutable_proof(self) -> None:
        manifest = valid_manifest()
        before = copy.deepcopy(manifest)
        candidate = candidate_for()
        repository = "crazysmile-PhD/downkyi-runtime-assets"
        tag = "ffmpeg-btbn-autobuild-2026-08-12-13-15"
        mirror = ffmpeg_assets.collect_mirror_evidence(
            candidate,
            repository,
            tag,
            release_readback_for(candidate, repository, tag),
            "2026-08-13T00:00:00Z",
        )
        mirror["readBack"]["immutable"] = False

        with self.assertRaisesRegex(ffmpeg_assets.AssetError, "not immutable"):
            ffmpeg_assets.apply_update(manifest, candidate, mirror)
        self.assertEqual(before, manifest)

    def test_readback_evidence_updates_only_the_selected_rid(self) -> None:
        manifest = valid_manifest()
        candidate = candidate_for()
        mirror = ffmpeg_assets.collect_mirror_evidence(
            candidate,
            "crazysmile-PhD/downkyi-runtime-assets",
            "ffmpeg-btbn-autobuild-2026-08-12-13-15",
            release_readback_for(
                candidate,
                "crazysmile-PhD/downkyi-runtime-assets",
                "ffmpeg-btbn-autobuild-2026-08-12-13-15",
            ),
            "2026-08-13T00:00:00Z",
        )
        updated = ffmpeg_assets.apply_update(manifest, candidate, mirror)
        self.assertEqual(mirror["assets"]["win-x64"], updated["ffmpeg"]["assets"]["win-x64"])
        self.assertEqual(manifest["ffmpeg"]["assets"]["linux-x64"], updated["ffmpeg"]["assets"]["linux-x64"])

    def test_readback_mismatches_reject_manifest_mutation(self) -> None:
        repository = "crazysmile-PhD/downkyi-runtime-assets"
        tag = "ffmpeg-btbn-autobuild-2026-08-12-13-15"
        candidate = candidate_for()
        file_name = candidate["assets"]["win-x64"]["files"][0]["mirroredFileName"]
        for field, value in (("name", "wrong-name.zip"), ("size", 129), ("sha256", digest("wrong"))):
            with self.subTest(field=field):
                manifest = valid_manifest()
                before = copy.deepcopy(manifest)
                mirror = ffmpeg_assets.collect_mirror_evidence(
                    candidate,
                    repository,
                    tag,
                    release_readback_for(candidate, repository, tag),
                    "2026-08-13T00:00:00Z",
                )
                mirror["readBack"]["assets"][file_name][field] = value
                with self.assertRaisesRegex(ffmpeg_assets.AssetError, "read-back .* mismatch"):
                    ffmpeg_assets.apply_update(manifest, candidate, mirror)
                self.assertEqual(before, manifest)

    def test_historical_mirror_manifest_is_valid(self) -> None:
        manifest = valid_manifest("btbn-autobuild-2025-01-01-00-00")
        ffmpeg_assets.validate_manifest(manifest)

    def test_preflight_reports_the_broken_rid(self) -> None:
        manifest = valid_manifest()
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), AssetRequestHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            for asset in manifest["ffmpeg"]["assets"].values():
                asset["url"] = f"http://127.0.0.1:{server.server_port}/available.zip"
                if "ffprobeUrl" in asset:
                    asset["ffprobeUrl"] = f"http://127.0.0.1:{server.server_port}/available.zip"
            manifest["ffmpeg"]["assets"]["linux-x64"]["url"] = f"http://127.0.0.1:{server.server_port}/missing.zip"
            with mock.patch.object(ffmpeg_assets, "validate_manifest"):
                with self.assertRaisesRegex(ffmpeg_assets.AssetError, "tool=ffmpeg\\nrid=linux-x64"):
                    ffmpeg_assets.preflight(manifest, 5)
        finally:
            server.shutdown()
            thread.join()
            server.server_close()


if __name__ == "__main__":
    unittest.main()
