# Path/String Helper Nullability Candidates

## 1. Purpose

This document is a discovery-only inventory for the **next narrow nullable cleanup PR** focused on path/string/helper code.

- This document does **not** change production behavior.
- This document does **not** claim warning counts.
- Candidate status should be re-checked against current build warnings when implementation starts.

## 2. Selection constraints for the next PR

The next implementation PR should:

- stay in one small helper area;
- avoid downloader/network/UI/DB behavior changes;
- preserve existing return-value contracts (`null`, empty string, fallback values);
- avoid broad refactors or signature changes unless required by warnings.

## 3. Candidate inventory (existing files)

| Candidate file | Current helper role | Nullable cleanup opportunity | Risk level | Notes for future implementation PR |
|---|---|---|---|---|
| `DownKyi.Core/Storage/StorageManager.cs` | Centralized path/directory resolver wrappers (`GetAriaDir`, `GetLogsDir`, etc.) | Validate directory argument flows and helper return assumptions around path creation wrappers. | Medium | Touch with care: this file is used broadly; keep behavior identical (same created paths and return strings). |
| `DownKyi.Core/Utils/ObjectHelper.cs` | String/URL/cookie parsing and file-serialization helper methods | Tighten nullable annotations for URL/query/cookie parsing boundaries and stream/file read return paths. | Medium | Keep existing error/fallback behavior (`null` or `false` on failure) unchanged. |
| `DownKyi.Core/Utils/StringLogicalComparer.cs` | String comparison helper with numeric segment ordering | Improve generic/string null-flow handling and remove nullable ambiguity in compare inputs. | Low-Medium | Should remain algorithm-preserving; comparator ordering behavior must not change. |
| `DownKyi.Core/Utils/Encryptor/Encryptor.String.cs` | Pure string encryption/decryption helper surface | Clarify nullability assumptions for input/key parameters and fallback returns on exceptions. | Low-Medium | Preserve current fallback semantics (return original input when crypto fails). |

## 4. Deferred / out-of-scope for the first path-string batch

These are related but should be deferred from the first narrow batch:

- `DownKyi.Core/Utils/Encryptor/Encryptor.File.cs` (file IO + crypto state; higher behavior risk).
- `DownKyi.Core/Storage/Database/SqliteDatabase.cs` (database-facing behavior).
- Any files under `DownKyi/` UI ViewModels/Views (binding and navigation risk).
- Downloader/network execution paths (non-helper side effects).

## 5. Suggested PR slicing

Recommended order for upcoming implementation PRs:

1. `fix: annotate path-string helper nullability (string comparer + object helper)`
2. `fix: annotate storage path helper nullability (storage manager only)`
3. Optional follow-up: evaluate encryptor string helper separately if warning pressure remains.

Each PR should remain behavior-neutral and independently reviewable.

## 6. Verification checklist for the future implementation PR

- Build succeeds after nullable changes.
- No runtime path output changes.
- No URL/cookie parsing behavior drift.
- No comparator ordering behavior drift.
- No schema/network/downloader/UI behavior touched.
