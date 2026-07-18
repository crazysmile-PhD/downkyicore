# Performance Baseline

Status: active baseline
Last reviewed: 2026-07-18

This document records reproducible performance evidence for the refactor. Measurements are evidence, not PR gates, until runner variance and representative datasets are understood.

## Microbenchmarks

Run from the repository root:

```powershell
dotnet run --project .\benchmarks\DownKyi.Benchmarks\DownKyi.Benchmarks.csproj -c Release -- --filter "*"
```

The initial suite measures:

- Bilibili request URL and query preparation.
- Representative API envelope JSON deserialization.
- Per-operation allocation counts through BenchmarkDotNet's memory diagnoser.

Benchmark results are written under `BenchmarkDotNet.Artifacts/` and are intentionally ignored by source control.

## Baseline 2026-07-10

Environment:

- Windows 11 x64
- AMD Ryzen 7 4800H, 8 cores / 16 logical processors
- .NET SDK 10.0.301 and .NET runtime 10.0.9
- BenchmarkDotNet 0.15.8 ShortRun, three warmups and three measured iterations
- Source baseline: `origin/main` at `1262bcd` plus the behavior-baseline working tree

| Operation | Mean | StdDev | Allocated |
| --- | ---: | ---: | ---: |
| Build request URL | 710.4 ns | 8.66 ns | 1,488 B |
| Deserialize API envelope | 492.9 ns | 4.71 ns | 144 B |

The URL preparation path allocates substantially more than the representative JSON parse. Treat this as an investigation lead, not proof that it limits download throughput; end-to-end traces must show that the path is hot before it is optimized.

PR 30-32 found no end-to-end trace showing request URL construction limits startup, transfer throughput, or UI projection. The 1,488 B/request result therefore remains a non-gating investigation lead; no speculative query-building rewrite was made.

## System Baselines

Run the complete quick suite from the repository root:

```powershell
dotnet run --project .\benchmarks\DownKyi.SystemBenchmarks\DownKyi.SystemBenchmarks.csproj -c Release -- --quick
```

Omit `--quick` for the nightly-sized datasets. Use `--scenario shell`, `ui`, `restore`, `sqlite`, `transfer`, or `ffmpeg` to isolate a single investigation, and use `--output <path>` to select the JSON report path.

The suite measures:

- cold and warm shell startup through the real Host, `MainWindow` resolution, and XAML load;
- peak working set while the real SQLite projection restores unfinished tasks;
- coalesced SQLite progress writes per task-minute with a deterministic clock;
- aggregate built-in downloader throughput at 1, 4, and 8 tasks against an in-process Range server;
- source samples, published UI progress updates, and resulting property notifications per second;
- actual FFmpeg CPU and available hardware-encoder concurrency plus sampled child-process working set.

The default `all` runner executes every scenario in a separate child process before merging the reports. This prevents Avalonia, SQLite pools, or encoder detection from contaminating another scenario and gives each working-set measurement a fresh process boundary.

Every report records runtime, OS, architecture, dataset size, downloader backend, and commit SHA. The scheduled `.github/workflows/system-benchmarks.yml` uploads one JSON artifact per runner OS. It fails when a scenario cannot execute or produce a report, but does not compare metric thresholds.

Do not compare ad-hoc stopwatch values from different machines. Record runtime, OS, architecture, dataset size, downloader backend, and commit SHA with every system baseline.

Loopback transfer throughput isolates local downloader scheduling and copying; it does not measure Bilibili or CDN capacity. A hardware encoder listed by FFmpeg is reported as available only after a real synthetic encode succeeds. Unsupported or unavailable GPU paths remain explicit and preserve the production CPU fallback rule.

## Windows Quick Baseline 2026-07-18

Environment: commit `0664617570e32b362e688cfa3e11c38aa79421f5`, .NET 10.0.10, Windows 10.0.26200, x64. This is a same-machine smoke baseline, not a cross-machine performance target.

| Scenario | Dataset | Result |
| --- | --- | --- |
| Shell startup | isolated empty profile; 2 warm iterations | cold 742.57 ms; warm median 5.24 ms |
| Unfinished restore | 50 SQLite tasks; 1,351,176-byte DB | 32.00 ms; 2,490,368-byte peak working-set delta |
| Progress persistence | 4 tasks; 5 simulated seconds; 400 source samples | 20 SQLite writes; 60 writes/task-minute |
| UI projection | 1,000 source samples/s | 11 published updates/s; 33 property notifications/s |
| Built-in transfer | 2 MiB/task loopback Range server | 1 task 55.72 Mbps; 4 tasks 671.38 Mbps; 8 tasks 1,075.79 Mbps |
| FFmpeg CPU | 2 jobs; concurrency 1 | 2 successful; 21,090,304-byte peak child working set |
| FFmpeg NVENC | 2 jobs; concurrency 1 | 2 successful; 167,690,240-byte peak child working set |

The committed report schema, rather than this one machine's values, is the long-term contract. Nightly artifacts remain the source for per-run evidence.
