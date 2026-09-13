# FFmpeg Asset Mirroring

## Ownership boundary

`script/assets/external-assets.json` is the sole production owner of every
FFmpeg/ffprobe URL and SHA-256. Package scripts only consume that manifest;
they do not query an upstream release API, resolve `latest`, select a version,
or fall back to an unverified executable.

The updater is the only component allowed to query upstream releases. Its
normal source policy is deliberately platform-specific:

| RID | Discovery source | Required flavor |
| --- | --- | --- |
| `win-x64`, `linux-x64`, `linux-arm64` | `BtbN/FFmpeg-Builds` GitHub Releases API | fixed `autobuild-*`, static GPL archive, `ffmpeg` and `ffprobe` |
| `win-x86` | `yt-dlp/FFmpeg-Builds` GitHub Releases API during the first mirror import | fixed `autobuild-*`, GPL archive with `ffprobe` |
| `osx-x64`, `osx-arm64` | existing reviewed martin-riedl pinned source during the first mirror import | separate FFmpeg/ffprobe archives, preserved without HTML scraping |

The scheduled updater changes only the BtbN RIDs. martin-riedl is not scraped:
it does not publish a suitable release API, so a macOS refresh is a deliberate
operator-reviewed bootstrap instead of a fragile HTML parser.

## Immutable mirror policy

The mirror repository is `crazysmile-PhD/downkyi-runtime-assets`. It must be a
dedicated, project-owned repository with GitHub Immutable Releases enabled
before the first bootstrap. Immutability applies only to releases published
after the setting is enabled. Each update
creates one new GitHub Release named `ffmpeg-<fixed-upstream-version>` and
uploads filenames that contain the RID, fixed upstream tag, and SHA-256 prefix.
The workflow creates a draft, uploads every archive, and only then publishes
the release. It never modifies or overwrites an existing release or asset,
never uses `latest`, and the repository retention policy is `never-delete`.
If a downstream manifest-PR step fails after publication, a retry may resume
only after the existing release reads back as immutable and every expected
asset name, size, and digest exactly matches the verified candidate.
The manifest update is fail closed unless the release API reads the published
release back with `immutable=true`.

Every production asset entry must use a fixed URL below that repository's
`releases/download/<tag>/` path, include an archive SHA-256, and retain
provenance: upstream repository/release/file/URL, mirror timestamp, target RID,
and FFmpeg build identifier. Historical tags and assets are release inputs for
old DownKyi commits and must not be pruned.

## Updater flow

`.github/workflows/update-ffmpeg-assets.yml` runs weekly from the repository
default branch. GitHub schedules always use the latest default-branch commit,
and a directly dispatched workflow must exist on that branch. Before this
workflow reaches `main`, bootstrap it through the existing **Build** workflow:
select `release/v1.1.1-integration`, set `update_ffmpeg_assets=true`, and set
`bootstrap_ffmpeg_assets=true`. The Build workflow is already registered on
the default branch and calls the updater from the selected integration ref.
The updater verifies that its checkout branch and manifest PR base are equal.

The updater then:

1. Discover the newest complete, non-`latest` GitHub release using the
   publisher API and its per-asset SHA-256 digest.
2. Download the selected archives, verify size and SHA-256, extract them, and
   require non-empty `ffmpeg` and `ffprobe` files.
3. On native runners run `ffmpeg -version`, `ffprobe -version`, and where
   applicable verify the required `h264_nvenc` encoder is compiled in.
4. Only after every matrix validation job succeeds and repository release
   immutability is confirmed, create a draft mirror release, upload the exact
   verified archives, and publish it.
5. Record the resulting fixed mirror URLs and provenance, validate them with
   the preflight, require immutable release read-back, then create a manifest
   PR against the same branch that supplied the manifest. The workflow cannot
   push `main`.

A validation, download, checksum, extraction, capability, upload, or preflight
failure stops before manifest mutation. A failed upload may leave an
unreferenced incomplete release for an operator to inspect, but it cannot
update the production manifest or replace a historical asset. Manifest PR
creation is path-scoped to `script/assets/external-assets.json`; downloaded
archives and updater evidence remain ignored workspace data.

## Bootstrap and recovery

Before enabling normal scheduled updates, create the dedicated repository,
enable GitHub Immutable Releases, and dispatch **Build** from
`release/v1.1.1-integration` with `update_ffmpeg_assets=true` and
`bootstrap_ffmpeg_assets=true`. This imports all six current pinned sources,
including the distinct win-x86/macOS sources, into one immutable mirror release
and opens a PR against that integration branch. After the updater reaches
`main`, direct dispatches and the weekly schedule use `main` as both checkout
and manifest base. Do not manually edit the manifest to another BtbN daily URL.

If the bootstrap fails:

1. Keep the current manifest unchanged.
2. Inspect the failed RID and source URL in the workflow output.
3. Correct an upstream policy issue or create a fresh bootstrap candidate; do
   not overwrite the partial mirror release/tag. If the release was fully
   published before a later step failed, a re-run may reuse it only as immutable
   read-back evidence after every expected asset matches exactly.
4. Re-run the dispatch. Review the generated PR and require the release/package
   workflow before merging.

To force a normal BtbN refresh, run the same workflow with `bootstrap=false`.
If the selected fixed release is already represented in the manifest, it exits
successfully without creating a PR.

## Required permissions and secrets

The workflow uses two narrowly scoped credentials:

- `RUNTIME_ASSETS_TOKEN`: fine-grained token or GitHub App installation token
  with **Contents: read/write** only on
  `crazysmile-PhD/downkyi-runtime-assets`. It creates releases and uploads
  assets, but has no DownKyi source-repository permission.
- `DOWNKYI_AUTOMATION_TOKEN`: fine-grained token or GitHub App installation
  token with **Contents: read/write** and **Pull requests: read/write** only on
  `crazysmile-PhD/downkyicore`. It creates the manifest PR. A separate token is
  required because a PR opened by `GITHUB_TOKEN` does not reliably trigger the
  repository's package CI.

No token is needed by normal builds or package downloaders.

## Local checks

Run these from the repository root with Python 3.12 or later. Python is also a
package-script prerequisite: the Windows and Unix FFmpeg downloaders invoke the
same fail-closed checksum verifier.

```powershell
python -m unittest script/tests/test_ffmpeg_assets.py -v
python script/ffmpeg-assets.py validate-manifest --manifest script/assets/external-assets.json
python script/ffmpeg-assets.py preflight --manifest script/assets/external-assets.json --timeout 30
```

`preflight` is intentionally only an availability and schema gate. The package
downloaders still rehash the downloaded archive before extraction, so a later
content replacement fails closed.
