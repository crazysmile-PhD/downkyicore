# Performance Baseline

Status: active baseline
Last reviewed: 2026-07-10

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

## Pending System Baselines

These measurements require the new Host, task store, orchestrator, and projection boundaries so tests can inject isolated data and deterministic clocks:

- cold and warm shell startup time;
- peak working set while restoring unfinished tasks;
- SQLite progress writes per task-minute;
- aggregate transfer throughput at 1, 4, and 8 concurrent tasks;
- UI progress notifications per second;
- FFmpeg CPU/GPU concurrency and peak memory.

Do not compare ad-hoc stopwatch values from different machines. Record runtime, OS, architecture, dataset size, downloader backend, and commit SHA with every system baseline.
