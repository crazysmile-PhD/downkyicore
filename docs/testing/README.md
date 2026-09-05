# Testing

測試分層：

- `DownKyi.Domain.Tests`：state transitions 與 value objects。
- `DownKyi.Application.Tests`：commands、queries、coordinators 與 ports。
- `DownKyi.Infrastructure.Tests`：SQLite、migration、write-behind 與 adapters。
- `DownKyi.Core.Tests`：Bilibili contracts、HTTP、settings、logging、FFmpeg、aria2。
- `DownKyi.Desktop.Tests`：real Host、XAML 與 typed navigation smoke tests。
- `DownKyi.Tests`：executable compatibility 與 end-to-end service tests。
- `DownKyi.Architecture.Tests`：重要 dependency direction 與 repository wiring。
- `DownKyi.Windows.Tests`：Windows process、Job Object 與 native handle 行為。
- `DownKyi.MacOS.Tests`：macOS system Bash、signing 與 packaging 行為。

## Formal Test Entry

`tools/DownKyi.CentralTestRunner` 是 repository 正式 test execution entry。
`script/test-project.ps1` 與 `script/test-solution.ps1` 經由
`script/test-project-runner.ps1` 呼叫它。Runner 擁有：

- 明確的 test-project allowlist 與 `DownKyiTestPlatforms` platform selection；
- canonical invocation 與 slice/test identity；
- `docs/testing/test-runner-policy.json` 中必要的 xUnit in-process routing；
- per-project TRX validation 與 target exit result。

正式 PowerShell boundary 每次先 build CentralTestRunner，再執行目前
repository state 的 runner。不要直接新增平行的 `dotnet test` / `vstest`
repository entry。

## Lightweight Flight Recorder

CentralTestRunner 從 test process 啟動時記錄 slice identity、root PID 與
start time，以及 exit、exit code、timeout、cancellation、bounded stop、
cleanup 和 bounded stdout/stderr tail。正常 PASS 會刪除 recorder evidence。

FAIL、timeout 或 abnormal cleanup 會保存 evidence，並取得一次 failure-time
best-effort process snapshot。Child rows 只表示當下觀察到的 PID、PPID 與
start time；沒有觀察到 child 不能解讀成證明 child 不存在。Recorder 是
diagnostic aid，不判斷 root cause、PrimaryFailure、causal precedence 或完整
descendant history。

Focused recorder behavior 位於
`tests/DownKyi.Architecture.Tests/CentralTestRunnerRecorderTests.cs`：一個
deterministic timeout fixture 證明失敗 evidence，另一路徑確認 PASS 不保留
大型 evidence。

短暫 sharing violation、resource busy、rename/move/overwrite 或 database-lock
失敗使用 [Targeted Resource Forensics](targeted-resource-forensics.md)。先對準
resource 與真實 operation，再用相同語義的 probe 找 failure window；只有直接
owner/lifecycle evidence 才能宣稱 root cause proven。不得先 blanket-enable
tracing、重跑相同失敗或加入 timing workaround。

## Test Isolation

測試不得讀取使用者真實 settings、cookie、下載 DB 或 aria2 session。網路
contract tests 使用 fixture 或 loopback server。OS-specific behavioral tests
必須位於對應 platform project；Architecture tests 不模擬另一個作業系統。

其他 authority locator：

- module dependency policy：`module-boundary-ratchets.md`
- dependency、binary 與 release maintenance：`../maintenance.md`
- formal verification 與 rollback：`../operations/verification-and-rollback.md`
