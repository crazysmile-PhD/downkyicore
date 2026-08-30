# Maintenance Guide

This file owns only current human maintenance procedures. Architecture facts,
versions, inventories, CI matrices and completed-run evidence are queried from
their repository or GitHub owners instead of being copied here.

## Dependency Updates

1. Update managed package versions in
   [Directory.Packages.props](../Directory.Packages.props). Update the SDK only
   through [global.json](../global.json).
2. Keep a dependency update separate from unrelated refactoring unless the
   refactoring is required by that dependency.
3. Run the canonical sequence in
   [verification-and-rollback.md](operations/verification-and-rollback.md).
4. Review both vulnerable and deprecated transitive dependency reports.
5. If an external compatibility assumption changes, update its existing design
   or operations owner and the deterministic contract test in the same change.

Do not copy current package versions or audit results into this file. The
metadata and CI artifacts are the queryable owners.

## Analyzer Maintenance

- Repository analyzer settings are owned by
  [Directory.Build.props](../Directory.Build.props) and
  [.editorconfig](../.editorconfig).
- Do not add project-wide `NoWarn`, analyzer exclusions, `#nullable disable`,
  broad `GlobalSuppressions.cs`, or severities of `none`/`silent` to make a
  build pass.
- An external-protocol suppression is acceptable only when the protocol
  requires the algorithm, a deterministic contract test proves it, and the
  source explains why it is not used for credentials or trust decisions.
- Generate diagnostic inventory with
  [analyzer-inventory.ps1](../script/analyzer-inventory.ps1) when an analyzer
  migration actually needs a review artifact. Do not maintain a second manual
  file-and-line inventory.

## External Binaries

[external-assets.json](../script/assets/external-assets.json) is the only owner
of external archive URLs, immutable source identity and checksums. Acquisition,
mirroring and package behavior are implemented by:

- [aria2.ps1](../script/aria2.ps1) and [aria2.sh](../script/aria2.sh);
- [ffmpeg.ps1](../script/ffmpeg.ps1) and [ffmpeg.sh](../script/ffmpeg.sh);
- [update-ffmpeg-assets.yml](../.github/workflows/update-ffmpeg-assets.yml);
- [build.yml](../.github/workflows/build.yml).

When updating an external asset:

1. Select an immutable upstream release or source commit. Never use a mutable
   `latest` URL.
2. Verify publisher metadata and digest before changing the manifest.
3. For project-built aria2, regenerate the canonical patch from the fixed
   official base, verify `git apply --check`, build every supported RID and
   record archive plus extracted-binary digests in the manifest.
4. For mirrored FFmpeg, use the existing mirror workflow and require capability
   validation before its manifest update PR is accepted.
5. Invoke installers from outside `script/` as well as the repository root;
   asset scripts must resolve their inputs relative to themselves.
6. Run the existing real-binary security/package gates and inspect their
   sanitized reports. A checksum proves artifact identity, not reproducible
   build provenance or publisher trust.
7. Confirm missing hardware acceleration still falls back to a supported
   software path.

Security assumptions and residual risk remain in
[aria2-security.md](operations/aria2-security.md); FFmpeg mirror procedure is
owned by [ffmpeg-asset-mirroring.md](operations/ffmpeg-asset-mirroring.md).

## Bilibili Contract Maintenance

The current endpoint/envelope inventory is
[bilibili-api-audit.md](operations/bilibili-api-audit.md). Endpoint changes
update that owner and a deterministic fixture together.

Anonymous or authenticated live probes require explicit operator intent. They
are time-point evidence only and never replace fixtures. Authenticated probes
must read credentials through the existing environment boundary, persist only
sanitized allowlisted metadata, and be followed by the repository secret scan.

## Release Maintenance

Release version identity is owned by [version.txt](../version.txt), while tag,
rehearsal, package validation and rollback procedure are owned by
[verification-and-rollback.md](operations/verification-and-rollback.md).
Do not copy package matrices, runner names or previous run results here.

External package validation must continue to prove non-empty executable,
aria2, FFmpeg and ffprobe content, expected application version, package
manifests and absence of user data. macOS signing/notarization trust claims must
match the exact mode and final artifact validated by the release workflow.

## Manual Regression Checklist

Use this checklist only for affected download, parsing, UI or exit behavior:

- Start and close the app; confirm the process and owned child processes exit.
- Reopen from isolated data and confirm the main window appears.
- Parse representative video, bangumi and course inputs covered by the change.
- Exercise selected, multi-part and all-item download admission when relevant.
- Cancel directory selection and confirm no task is created.
- Pause, close and reopen a large task; confirm resume identity is retained.
- Delete an active task and confirm only task-owned output and sidecars are
  removed.
- Validate subtitle/media output with the existing deterministic tools.
- Export diagnostics and confirm credentials, identifiers and complete personal
  paths are redacted.

Record execution evidence in the PR or workflow artifact for the exact commit;
do not append it to this guide.
