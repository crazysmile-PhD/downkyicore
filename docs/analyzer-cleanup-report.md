# Analyzer Cleanup Report

This report preserves the original inventory in `docs/analyzer-baseline.md` and records the verified PR 02 cleanup result. Counts are deduplicated by rule, project, file, location, and message.

## Result

- Before: **1,654 unique diagnostics across 71 rules**.
- After: **0 unhandled CA diagnostics across the full solution**.
- Enforcement: **77 cleaned rules are explicit errors**, including six rules that emerged while refactoring (`CA1014`, `CA1812`, `CA1826`, `CA1846`, `CA1852`, and `CA5369`).
- Repository defaults: `EnableNETAnalyzers=true`, `AnalysisMode=All`, `EnforceCodeStyleInBuild=true`, and `CodeAnalysisTreatWarningsAsErrors=true`.

## Rule Counts

| Rule | Before | After |
| --- | ---: | ---: |
| `CA1001` | 14 | 0 |
| `CA1002` | 183 | 0 |
| `CA1003` | 7 | 0 |
| `CA1008` | 9 | 0 |
| `CA1012` | 1 | 0 |
| `CA1014` | 0 | 0 |
| `CA1024` | 5 | 0 |
| `CA1030` | 3 | 0 |
| `CA1031` | 113 | 0 |
| `CA1032` | 2 | 0 |
| `CA1034` | 4 | 0 |
| `CA1051` | 82 | 0 |
| `CA1052` | 4 | 0 |
| `CA1054` | 18 | 0 |
| `CA1055` | 1 | 0 |
| `CA1056` | 32 | 0 |
| `CA1058` | 1 | 0 |
| `CA1062` | 121 | 0 |
| `CA1063` | 2 | 0 |
| `CA1303` | 3 | 0 |
| `CA1304` | 21 | 0 |
| `CA1305` | 89 | 0 |
| `CA1307` | 49 | 0 |
| `CA1308` | 2 | 0 |
| `CA1309` | 1 | 0 |
| `CA1310` | 26 | 0 |
| `CA1311` | 21 | 0 |
| `CA1507` | 19 | 0 |
| `CA1508` | 2 | 0 |
| `CA1513` | 1 | 0 |
| `CA1515` | 189 | 0 |
| `CA1707` | 45 | 0 |
| `CA1708` | 2 | 0 |
| `CA1711` | 3 | 0 |
| `CA1720` | 1 | 0 |
| `CA1724` | 10 | 0 |
| `CA1802` | 1 | 0 |
| `CA1805` | 6 | 0 |
| `CA1810` | 4 | 0 |
| `CA1812` | 0 | 0 |
| `CA1813` | 1 | 0 |
| `CA1816` | 1 | 0 |
| `CA1819` | 2 | 0 |
| `CA1820` | 13 | 0 |
| `CA1822` | 36 | 0 |
| `CA1823` | 1 | 0 |
| `CA1826` | 0 | 0 |
| `CA1829` | 1 | 0 |
| `CA1845` | 3 | 0 |
| `CA1846` | 0 | 0 |
| `CA1847` | 1 | 0 |
| `CA1849` | 10 | 0 |
| `CA1850` | 3 | 0 |
| `CA1852` | 0 | 0 |
| `CA1854` | 3 | 0 |
| `CA1859` | 5 | 0 |
| `CA1861` | 6 | 0 |
| `CA1862` | 9 | 0 |
| `CA1864` | 4 | 0 |
| `CA1866` | 3 | 0 |
| `CA1872` | 2 | 0 |
| `CA2000` | 21 | 0 |
| `CA2007` | 259 | 0 |
| `CA2008` | 2 | 0 |
| `CA2025` | 1 | 0 |
| `CA2100` | 3 | 0 |
| `CA2201` | 6 | 0 |
| `CA2211` | 2 | 0 |
| `CA2213` | 1 | 0 |
| `CA2214` | 4 | 0 |
| `CA2227` | 141 | 0 |
| `CA2234` | 3 | 0 |
| `CA2263` | 1 | 0 |
| `CA5351` | 6 | 0 |
| `CA5369` | 0 | 0 |
| `CA5373` | 1 | 0 |
| `CA5401` | 2 | 0 |

## Defects Fixed

- Replaced catch-all exception sinks, synchronous async waits, unbounded background loops, and undisposed process/stream ownership with explicit lifecycle and cancellation contracts.
- Preserved Bilibili WBI and legacy settings migration while restricting weak-crypto suppressions to the exact required calls.
- Preserved JSON, XML, SQLite, Avalonia binding, enum numeric, aria2, and NFO contracts while correcting API shape and collection ownership.
- Removed runtime-random BVID/codec hashes from DURL identity; DURL descriptors are ordered and use deterministic `DURL.Order` download keys.
- Removed raw QR login addresses from terminal and diagnostic logs.
- Made protocol tokens, SRT/ASS output, NFO values, file names, version parsing, aria2 options, and diagnostic timestamps culture-invariant; user-facing number/date displays explicitly retain current-culture behavior.
- Split public BenchmarkDotNet cases from the internal runner and moved public NFO XML DTOs into a library assembly so reflection-based tools continue to work.

## Approved Exceptions

| Rule | Location | Reason | Guard |
| --- | --- | --- | --- |
| `CA5351` | `DownKyi.Core/BiliApi/Sign/WbiSign.cs` | Bilibili WBI requires MD5 for its external `w_rid` wire protocol; it is not password storage or a local trust decision. | `WbiSignTests.EncodeWbiMatchesProtocolVector` |
| `CA5351` | `DownKyi.Core/Utils/Encryptor/LegacySettingsDecryptor.cs` | Read-only migration of settings from DownKyi 1.0.20 and earlier; new settings are not written with MD5-derived encryption. | `LegacySettingsDecryptorTests.DecryptReadsLegacySettingsFixture` |

No project-wide `NoWarn`, `GlobalSuppressions.cs`, `#nullable disable`, analyzer exclusion, or `.editorconfig` `none`/`silent` severity is present.

## Test Coverage

- Full solution tests: **104 passed** on the local .NET 10 Release build.
- Full solution cross-RID builds passed with zero warnings for `linux-x64` and `osx-x64`; native Linux/macOS test execution remains the responsibility of the matching CI runners.
- Added or extended contracts for WebClient retry/cancellation, JSON wire names, XML/NFO round trips, aria2 lifecycle, DURL stable identity/order, and culture-invariant SRT/file-name output.
- Avalonia headless smoke starts the real Host and resolves MainWindow plus key ViewModels without Prism global container state; layer tests cover typed results, cancellation propagation, the system clock boundary, and unchanged user-data paths.
- Benchmark runner executes public non-sealed cases and requires an actual result row, not only exit code zero.

## Final Gate

```powershell
dotnet restore ./DownKyi.sln
dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental `
  -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true `
  -p:EnableNETAnalyzers=true `
  -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true
dotnet test ./DownKyi.sln -c Release --no-restore --no-build
dotnet format ./DownKyi.sln --verify-no-changes --no-restore
git diff --check
```

`.github/workflows/quality.yml` runs the same warning-as-error policy on Windows, Linux, and macOS.
