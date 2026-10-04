# Performance Measurement Contract

Performance reports are evidence, not portable thresholds. Current numbers belong to ignored benchmark output and CI artifacts；this document only defines how to produce and compare them.

## Entrypoints

Microbenchmarks：

```powershell
dotnet run --project .\benchmarks\DownKyi.Benchmarks\DownKyi.Benchmarks.csproj `
  -c Release -- --filter "*"
```

System scenarios：

```powershell
dotnet run --project .\benchmarks\DownKyi.SystemBenchmarks\DownKyi.SystemBenchmarks.csproj `
  -c Release -- --quick
```

Omit `--quick` for nightly-sized datasets. Use `--scenario shell|ui|restore|sqlite|transfer|ffmpeg|logging` to isolate one owner；use `--output <path>` for the JSON report. The default `all` runner executes scenarios in separate child processes so Avalonia、SQLite pools、encoder discovery and working-set measurement do not contaminate one another.

BenchmarkDotNet writes under ignored `BenchmarkDotNet.Artifacts/`. Manually dispatched system runs upload one JSON artifact per runner OS；workflow failure means the scenario failed to execute or report, not that a metric crossed a hidden threshold.

## Report contract

Every comparable report must record：

- exact commit SHA and dirty-worktree state；
- runtime／SDK、OS、architecture and hardware identity；
- scenario、dataset size／shape、iteration／warmup policy；
- backend and relevant bounded-concurrency settings；
- raw metric units、sample count、failure／unsupported status；
- output schema version and artifact locator.

System scenarios cover shell startup、SQLite restore／progress persistence、UI projection、loopback built-in transfer、real FFmpeg CPU／available hardware encode, and production logging／flush. Source and report schema in `benchmarks/DownKyi.SystemBenchmarks` are authoritative；this list is only a route.

## Comparison rules

1. Compare only the same scenario、schema、dataset、runtime、OS、architecture、backend and relevant hardware. Cross-machine stopwatch values are not comparable.
2. Use repeated samples and report range／distribution；one quick run is a smoke result, not a regression threshold.
3. A change in semantics、dataset or instrumentation creates a new series. Do not splice it into an old baseline.
4. Loopback transfer measures local scheduling／copying, not Bilibili or CDN throughput.
5. FFmpeg hardware support is available only after a real synthetic encode succeeds. Unsupported GPU paths remain explicit and preserve CPU fallback.
6. Logging benchmarks must use the production provider and identical queue／recent-buffer dataset before comparing sink changes. Reliability, drops, flush latency and allocation are reported together；one metric cannot hide another.
7. Before turning a metric into a gate, demonstrate representative workload relevance and stable runner variance. Until then, label the result investigation evidence and keep current values in artifacts／Git history.

Formal verification and release gates are owned by `docs/operations/verification-and-rollback.md`；performance documents do not introduce a second pass/fail policy.
