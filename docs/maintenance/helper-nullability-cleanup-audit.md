# Helper Nullability Cleanup Audit

## 1. Purpose

This document identifies low-risk helper files and helper areas that are candidates for future nullable warning cleanup.

This PR is documentation-only.
It does not change runtime behavior.
It does not add suppressions.
It does not fix nullable warnings yet.
Future nullable cleanup should start with pure helper classes before services, UI, storage, or download flows.

## 2. Candidate selection rules

- Prefer static helper classes.
- Prefer deterministic helpers with stable input/output behavior and no side effects.
- Prefer files with no UI binding behavior.
- Prefer files with no database/schema serialization behavior.
- Prefer files with no network side effects.
- Prefer files with no downloader state transitions.
- Avoid ViewModels and Avalonia binding paths.
- Avoid DownloadService / Aria2 / built-in downloader flows.
- Avoid storage/database model classes in the first batch.
- Avoid public API signature changes unless reviewed separately.

## 3. Candidate table

| Candidate area | Why it may be safe | Risks to check before implementation | Recommended future PR |
|---|---|---|---|
| Formatting helpers | Usually pure value-to-string conversions with minimal side effects. | Confirm no localized fallback changes and no formatting output contract drift. | `fix: annotate pure helper nullability (formatting helpers)` |
| Path/string helpers | Often deterministic string/path composition and parsing utilities. | Confirm path normalization behavior remains identical across OS targets. | `fix: annotate pure helper nullability (path-string helpers)` |
| File-extension helpers | Typically simple extension parsing/mapping logic with narrow scope. | Verify existing unknown-extension fallback behavior is preserved. | `fix: annotate pure helper nullability (file-extension helpers)` |
| Pure conversion helpers | Usually stateless conversion logic between value types/DTO-like helper values. | Confirm null-input semantics and default return behavior remain unchanged. | `fix: annotate pure helper nullability (conversion helpers)` |
| Validation helpers | Often isolated boolean/rule checks without IO or async side effects. | Ensure validation rule precedence and error text outputs do not change. | `fix: annotate pure helper nullability (validation helpers)` |
| FFmpeg output validation helpers (isolated only) | Safe only when limited to parsing/validation of output metadata strings. | Confirm no process execution, command assembly, or retry logic is touched. | `fix: annotate pure helper nullability (isolated ffmpeg output validators)` |
| Settings-independent helpers | Utility helpers not coupled to settings persistence or schema serialization. | Verify no implicit dependency on settings defaults or migration paths. | `fix: annotate pure helper nullability (settings-independent helpers)` |

> Note: This audit intentionally does not claim exact warning counts. Warning quantities should only be stated when verified from build logs.

## 4. Non-candidates for first nullable batch

The following areas should not be included in the first nullable cleanup PR:

- DownloadService
- AriaDownloadService
- BuiltinDownloadService
- storage/database entities
- settings serialization
- Avalonia views/viewmodels
- command handlers
- async task orchestration
- FFmpeg process execution paths

## 5. Future implementation plan

Recommended next implementation PR title:

- `fix: annotate pure helper nullability`

Future PR rules:

- one helper area only
- no behavior changes
- no schema changes
- no UI binding changes
- no package updates
- no suppressions unless justified
- build must pass

## 6. Acceptance checklist for future nullable helper PR

- nullable warnings reduced in targeted helper area
- no public behavior changes
- no broad null-forgiving `!` usage
- no global suppressions
- no database/settings schema changes
- no UI binding changes
- no downloader behavior changes
- no FFmpeg process behavior changes
- GitHub Actions PR Build passes
