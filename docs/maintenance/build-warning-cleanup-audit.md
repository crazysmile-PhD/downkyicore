# Build Warning Cleanup Audit Plan

## 1. Purpose

This document defines the planning baseline for build warning cleanup after the download-stability batch was completed and closed.

This PR does not change runtime behavior.
Future warning cleanup should be split into narrow PRs.
CI currently builds successfully but emits many warnings.
The goal is to reduce warning noise without introducing behavioral changes.

## 2. Warning cleanup principles

- Do not mix warning cleanup with feature changes.
- Do not mix nullable cleanup with package upgrades.
- Do not silence warnings globally unless justified.
- Prefer fixing root causes over suppressing warnings.
- Keep each future PR scoped to one warning family or one subsystem.
- Do not change public APIs unless required and reviewed.
- Preserve serialization/database compatibility.
- Preserve UI binding behavior.

## 3. Suggested warning categories

| Category | Examples | Risk | Suggested PR strategy |
|---|---|---|---|
| Nullable reference warnings | `CS8600`, `CS8602`, `CS8618`, `CS8625` in helper/service/viewmodel code | Medium | Split by subsystem and review null-flow changes with minimal logic edits. |
| Unused variables / unused usings | `CS0168`, `CS0219`, IDE unused-using diagnostics | Low | Batch-remove only obvious dead locals/usings with no behavior impact. |
| Async warnings | `CS1998`, fire-and-forget misuse patterns, missing awaits | Medium | Handle one module at a time; verify command/event execution paths are unchanged. |
| Obsolete API warnings | `CS0618` on legacy APIs and framework methods | Medium to High | Triage by API usage and migrate only after behavior is understood and documented. |
| Platform compatibility warnings | `CA1416` and OS-guard-related analyzer warnings | Medium | Add targeted guards or isolate platform-specific calls per subsystem. |
| Possible null dereference in UI models | Nullability warnings in Avalonia UI model/viewmodel binding paths | Medium to High | Resolve in dedicated UI-focused PRs; validate bindings and default state behavior. |
| Possible null dereference in services | Nullability warnings in storage/download/network/core services | Medium to High | Split by service area (storage/download/network) and keep changes narrow. |
| Serialization / database model nullability warnings | Nullability mismatches in DTO/entity/settings models | High | Fix incrementally without schema or wire-format changes; require compatibility review. |
| XAML / Avalonia binding-related warnings (if present) | Binding path/type/nullability diagnostics from XAML compile/build | Medium | Address by view/viewmodel pair and manually verify target screens. |
| Analyzer/style warnings (if present) | IDE/CA style or maintainability diagnostics | Low to Medium | Tackle separately from nullable fixes; prefer auto-fix-safe, minimal-diff batches. |

## 4. Recommended cleanup order

1. Documentation-only warning audit.
2. Remove unused usings and obviously unused locals.
3. Fix isolated nullable warnings in pure helper classes.
4. Fix nullable warnings in DTO/model classes without schema changes.
5. Fix nullable warnings in services one subsystem at a time.
6. Fix UI/viewmodel warnings separately.
7. Handle obsolete APIs only after behavior is understood.
8. Consider targeted suppressions only after failed root-cause fixes.

## 5. First safe implementation PR candidates

- `chore: remove unused usings and locals`
- `fix: annotate pure helper nullability`
- `fix: harden storage nullability without schema changes`
- `fix: harden download-service nullability in small batches`
- `fix: harden ffmpeg helper nullability`
- `docs: document warning cleanup progress`

## 6. Forbidden future batch combinations

Future warning cleanup PRs must not combine warning cleanup with:

- runtime feature changes
- package updates
- CI redesign
- database schema changes
- downloader behavior changes
- FFmpeg behavior changes
- UI redesign
- broad formatting changes
- mass suppressions

## 7. PR acceptance checklist for future warning cleanup

- PR changes one warning family or one subsystem only.
- Build still passes.
- No public behavior changes unless explicitly stated.
- No database/settings schema changes.
- No package updates.
- No unrelated formatting churn.
- Warning count should go down or warning category should be better documented.
- Any suppression must explain why fixing root cause is unsafe.

## Forbidden changes in this PR

Do not:

- modify production code
- modify tests
- update packages
- modify CI
- modify DownloadService behavior
- modify FFmpeg behavior
- modify Aria2 behavior
- modify built-in downloader behavior
- modify WebClient behavior
- modify UI
- modify database/settings schema
- add warning suppressions
- fix nullable warnings in this PR

## Progress log

- PR #XX: removed unused usings and obviously unused locals without behavior changes.
