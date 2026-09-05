# CA1506 Architecture Audit

`script/audit-code-metrics.ps1` enables CA1506 only for an isolated full-solution
build. It disables warning promotion for that build. The C#
`tools/DownKyi.CodeMetricsAudit` tool consumes compiler SARIF, deduplicates by
rule, project, file and source location, and emits:

- `artifacts/code-metrics/ca1506-report.md`
- `artifacts/code-metrics/ca1506-report.json`

Findings are advisory. A finding count greater than zero exits successfully;
build failure, missing or malformed inputs, missing SARIF, parse failure, or an
unwritable report exits non-zero. Normal Release builds retain warnings-as-errors
and do not enable CA1506.

The JSON and Markdown reports are staged and published as one operation. If
either publication fails, the writer removes the operation's partial output and
restores any preceding complete report pair. Git provenance includes untracked
files because SDK projects can compile an untracked source file by default.

## Language boundary

PowerShell is retained only as an extremely thin process boundary. The audit
must finish the specially configured solution build before starting the C#
reporter. Having a running reporter rebuild the solution that contains its own
binary creates a Windows file-lifecycle conflict; a C# self-shadow-copy bootstrap
would add substantially more process and cleanup ownership than this wrapper.

The PowerShell entry may only resolve paths, forward parameters, invoke the
solution build, invoke the C# tool, preserve exit codes, and safely clean its
temporary SARIF directory. It must not parse SARIF or JSON, deduplicate or
classify findings, inspect Git, or generate reports. Those responsibilities are
owned by C# and covered by architecture tests. If another cross-platform entry
can provide the same ordered process boundary without self-rebuild complexity,
PowerShell is not otherwise required.

## Current inventory

The issue #194 implementation inventory on `main` at
`449086fe9c993d6aaf762bb96333b11f8338ab3f` contains 13 unique findings: five
production findings and eight test findings.

| Scope | Finding | Classification | Decision |
| --- | --- | --- | --- |
| production | `FfmpegConcatRuntime.ConcatAsync` | architecture hotspot | Keep advisory; behavior-led review is required before changing its process, validation, and cleanup boundary. |
| production | `SettingsManager` | needs manual review | The internal partial settings aggregate inflates the type metric; no product defect or safer owner split is established. |
| production | `Aria2TransferBackend.TransferAsync` | architecture hotspot | Keep advisory; do not disturb RPC, resume identity, progress, TLS, or typed failure behavior for a metric. |
| production | `BuiltinTransferBackend.TransferAsync` | architecture hotspot | Keep advisory; do not disturb HTTP, filesystem, resume, progress, or typed failure behavior for a metric. |
| production | `DownloadRuntimeFactory.Create` | composition root | Retain as the deliberate runtime assembly boundary; do not introduce thin wrappers for the metric. |
| test | `UiSmokeTests` and three methods in that class | test integration | Broad coupling is expected from real Host/XAML integration coverage. |
| test | `DownloadArtifactStageTests.CreateAsync` | test integration | Test fixture composition, not production runtime coupling. |
| test | `DownloadBootstrapHostedServiceTests.StartupQueuesPersistedAndInterruptedTasksWithoutUiPolling` | test integration | Test fixture composition, not production runtime coupling. |
| test | `DownloadPipelineCommitBoundaryTests.CreateAsync` | test integration | Test fixture composition, not production runtime coupling. |
| test | `DownloadTaskProjectionStoreResumeTests.AddDownloadingPreservesResumeIdentityFilesAndPausedStateAcrossReopen` | test integration | Test fixture composition, not production runtime coupling. |

There are currently no framework-driven CA1506 findings. CA1501 remains outside
the blocking policy, so normal Avalonia inheritance is not refactored or
suppressed to manufacture a zero baseline.

Production classification decisions live in
`script/code-metrics/ca1506-classifications.json` and are keyed by repository
path plus the diagnostic's repository-relative source region (`location:line:column`).
This distinguishes same-named members, overloads, and members on different declaring
types without depending on SARIF result order. A second diagnostic in the same production file
does not inherit an existing review. New test findings are classified as test
integration; new production findings default to needs manual review so the
report stays complete without treating an unreviewed metric as a defect.
