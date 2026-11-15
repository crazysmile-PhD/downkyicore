# Targeted Resource Forensics

Use this only for short-lived resource-contention failures. The goal is direct `Who + When + What Operation` evidence, not a rerun that turns green.

## H3: operation probe

Probe the operation that actually failed；a `Directory.Delete` sharing violation requires DELETE-access semantics, not a write-file substitute. The Windows directory probe uses `CreateFileW` with：

- `dwDesiredAccess = DELETE (0x00010000)`；
- `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`；
- `OPEN_EXISTING` and `FILE_FLAG_BACKUP_SEMANTICS`.

The probe opens and immediately closes a handle；it never deletes、moves or mutates the target. Keep only bounded state transitions、UTC／relative timestamp and Win32 error：

```text
Allowed -> SharingViolation -> Allowed
```

`Allowed -> SharingViolation` is an anomalous reversal. `cleanup-returned + SharingViolation` means cleanup has not satisfied the real operation contract. The last blocked／first allowed interval is evidence of a race window；do not hide it with sleep、retry or a longer magic timeout.

Rename、move、overwrite、database-lock and socket investigations need their own semantically exact probe.

## H4: pre-armed recorder

1. **Target**：declare the resource、real operation and failure timestamp.
2. **Probe**：start a low-impact non-destructive probe before teardown.
3. **Window**：derive the narrow blocked／allowed transition window.
4. **Pre-arm**：start a bounded memory recorder before cancellation；post-failure scanning is too late.
5. **Freeze and tail**：on `cleanup-returned + blocked`, preserve pre-window and continue only to the short ready-for-operation tail.
6. **Correlate**：restrict evidence to target path／file object、PID／PPID、command line、process start/end and file create/cleanup/close.
7. **Classify**：root cause is proven only when the failure window identifies an owner or explicit lifecycle ordering. Otherwise report `Root cause not proven.`

Windows opt-in recording uses bounded, non-interactive `wpr.exe` memory logging and `tracerpt.exe`. Successful runs without anomaly discard the trace. Never correlate globally by `IrpPtr` alone；pointer reuse admits unrelated events.

`DownKyi.TestInfrastructure.TargetedResourceForensics` owns delete-access transitions、timestamps、known-process correlation、failure-only preservation and sanitization. It is test／CI infrastructure；production code must not call it, and instrumentation must not change cancellation or cleanup semantics.

## CI and artifact contract

Run the controlled Windows fixture locally with `script/test-ci-infrastructure.ps1` when investigating resource ownership. Ordinary PR failures retain TRX and the runner's bounded failure evidence; enable targeted tracing only for a concrete resource investigation.

A preserved artifact includes：

- run／attempt／job and test identity；
- sanitized target and requested operation；
- probe start、cancellation、cleanup return、failure／anomaly and ready timestamps；
- state transitions and native errors；
- PID／PPID、process start/end and sanitized command identity；
- target create／cleanup／close and file-object identity；
- root-cause status, defaulting to `Root cause not proven.`

Never upload full ETL／XML, credential, user home path or unrelated machine events. Temporary product-test instrumentation belongs to the diagnosing PR and is removed with it；the controlled validation、classifier and reusable infrastructure remain.
