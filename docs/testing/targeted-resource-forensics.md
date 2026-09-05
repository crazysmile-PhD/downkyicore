# Targeted Resource Forensics

Targeted Resource Forensics 用於 CI 中短暫且難以重現的 resource-contention
失敗。它的目的不是讓測試重跑後變綠，而是在失敗窗口取得
`Who + When + What Operation` 的直接證據。

## H3：Continuous Operation Probe

Probe 必須測量真正失敗的 operation，不能以不同語義的操作替代。例如
`Directory.Delete` sharing violation 的 probe 必須測 DELETE-access semantics，
不能只測目錄內能否寫檔。

Windows directory DELETE-access probe 使用：

- `CreateFileW`
- `dwDesiredAccess = DELETE (0x00010000)`
- `dwShareMode = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`
- `dwCreationDisposition = OPEN_EXISTING`
- `dwFlagsAndAttributes = FILE_FLAG_BACKUP_SEMANTICS`

Probe 只取得 handle 並立即關閉，不刪除、搬移或修改目標。窄窗口 recorder
只保留 state transition、UTC timestamp、相對時間與 Win32 error：

```text
Allowed -> SharingViolation -> Allowed
```

`Allowed -> SharingViolation` 是異常反轉；`cleanup-returned + SharingViolation`
表示 cleanup completion contract 尚未滿足真正操作。最後一個 blocked 與第一個
allowed 的時間差是 race window，不得用 sleep 或 retry 把它隱藏。

這個模式可套用到 rename、move、overwrite、database lock 或 socket/port
ownership，但每一種 resource 都必須實作與真實失敗相同的 operation probe。

## H4：Pre-armed Resource Flight Recorder

調查順序固定如下：

1. **Target**：指定 resource、真正 operation 與 failure timestamp。
2. **Probe**：在 teardown 前啟動低干擾、非破壞性 probe。
3. **Window**：用 transition 找出 blocked/allowed 或 allowed/blocked 的窄窗口。
4. **Pre-arm**：在 cancellation 前啟動 memory-backed circular recorder；不得等失敗後才掃描。
5. **Freeze and tail**：`cleanup-returned + blocked` 時保留 pre-window，繼續到 ready-for-operation 的短尾端後停止。
6. **Correlate**：只關聯目標 path/file object、PID、PPID、command line、process start/end 與 file create/cleanup/close。
7. **Evidence threshold**：只有 failure window 的 owner 或明確 lifecycle ordering 才能把 root cause 標成 proven。

普通 failure-time 全表 handle scan 對數十毫秒窗口通常太慢。Windows hosted
runner 的 opt-in recorder 使用 `wpr.exe` memory logging 與 `tracerpt.exe`，
全程非互動、不接受 EULA、不要求使用者點擊 UAC。其 kernel buffers 有界，
成功且無 anomaly 時取消並丟棄 trace。輸出只保留目標 resource/file-object 與
相關 process lifecycle；不可用全域 `IrpPtr` 字串比對，因為 pointer reuse 會把
大量無關事件誤納入 artifact。

`DownKyi.TestInfrastructure.TargetedResourceForensics` 提供：

- `ProbeDeleteAccess`
- DELETE state transition 的 bounded buffer
- cancellation、cleanup-return 與 ready timestamp
- known PID 與 ancestor process lifecycle correlation
- file create/cleanup/close correlation
- failure-only artifact preservation 與 path/credential sanitization

它是測試與 CI 診斷能力，不得由 production code 呼叫，也不得改變 cancellation
或 cleanup semantics。

## Reference case: PR #220 H4

Windows hosted run `33950806618`、commit
`152d5e60a84e77689579169cb3e78dca9e4515da` 的 focused filesystem fixture
捕捉到以下直接時間線：

```text
06:51:18.9718669Z  PID 3396 opened Fixture.Tests, ShareAccess=3
06:51:19.0717172Z  cancellation requested
06:51:19.0825388Z  DELETE probe failed, STATUS_SHARING_VIOLATION
06:51:19.0825604Z  cleanup returned
06:51:19.0900640Z  PID 3396 Process/End
06:51:19.0902098Z  target FileObject Cleanup
06:51:19.0902195Z  target FileObject Close
06:51:19.0951128Z  DELETE access succeeded
```

Focused 結論是 **proven**：已知 root owner 在 cleanup return 後才出現
Process/End 與 target FileObject Cleanup/Close；`Process.WaitForExitAsync` 的
observable exit 早於 Windows directory delete readiness。這個 case 排除了
focused fixture 中的 late-born descendant、MSBuild node 與 testhost owner。

這個結論不能外推到原始
`CentralTestRunnerCommandTests.BuildCancellationReturns130AfterStoppingOwnedBuildProcess`
的偶發 exit code 2。該 e2e 在同一 run 通過，沒有 failure-window direct
evidence，因此其狀態仍是：**Root cause not proven.**

永久 controlled validation 會故意在已知 owner 仍存活時標記
cleanup-return，藉此建立 deterministic blocked window，再驗證 recorder 能把該
owner 的 PID/parent/command line、Process/End、FileObject Cleanup/Close 與
blocked-to-allowed transition 關聯起來。這是 recorder 的測試注入，不是
production cleanup contract，也不是 #220 e2e root-cause evidence。

## CI policy and artifacts

`.github/workflows/quality.yml` 只在 Windows controlled fixture 明確設定
`DOWNKYI_TARGETED_RESOURCE_FORENSICS=1` 時啟動 recorder。一般 test failure
之後，`script/classify-resource-contention.ps1` 只分類 TRX/log signature；沒有
resource-contention signature 就不建立 artifact，也不啟動 tracing。分類結果只
提供下一步 hint，不能宣告 root cause。

Forensic artifact 至少包含：

- run / attempt / job 與 test identity
- sanitized target resource 與 requested operation
- probe start、cancellation、cleanup return、failure/anomaly 與 ready timestamp
- DELETE state transitions 與 Win32 errors
- PID、parent PID、process start/end、command line
- target file/directory create、cleanup、close 與 file-object identity
- root-cause status，預設為 `Root cause not proven.`

完整 ETL/XML、credential、使用者 home path 或整台機器的無關事件不得上傳。
PR #220 的 temporary test instrumentation 必須在 production fix 完成後移除；
本文件、controlled validation、classifier 與 reusable test infrastructure 保留。
