# Testing

測試分層：

- `DownKyi.Domain.Tests`：state transitions 與 value objects。
- `DownKyi.Application.Tests`：commands、queries、coordinators 與 ports。
- `DownKyi.Infrastructure.Tests`：SQLite、migration、write-behind 與 adapters。
- `DownKyi.Core.Tests`：Bilibili contracts、HTTP、settings、logging、FFmpeg、aria2。
- `DownKyi.Desktop.Tests`：real Host、XAML 與 typed navigation smoke tests。
- `DownKyi.Tests`：目前 executable compatibility 與 end-to-end service tests。
- `DownKyi.Architecture.Tests`：依賴方向、禁止模式、AI environment 與 debt ratchets。
- `DownKyi.Windows.Tests`：Windows process、Job Object 與 native handle 行為。
- `DownKyi.MacOS.Tests`：macOS system Bash、signing 與 packaging 行為。

每個 `*.Tests.csproj` 必須以 `DownKyiTestPlatforms` 明確列出
`Windows`、`Linux`、`macOS` 的適用集合；全平台 project 必須三項全列，
沒有 default 或 `cross-platform` 別名。MSBuild、
`script/test-solution.ps1` 與 Assembly Lifecycle runner 都會先驗證完整
project inventory，再只執行明確包含目前 runner OS 的項目；
遺漏、條件式或無效 ownership 會 fail closed。OS-specific behavioral tests
必須位於對應 platform project；Architecture tests 只驗證 ownership、workflow
wiring 與靜態 release invariant，不模擬另一個作業系統。

目前 audit 已將 macOS signing 行為移至 `DownKyi.MacOS.Tests`，並將 Windows
Job Object 與 startup secret handle 行為移至 `DownKyi.Windows.Tests`。
TLS runtime、certificate storage、path comparison 與 Unix file-mode assertions
仍是刻意跨平台執行的 platform-adaptive coverage，不是整個 test 的 OS skip。

正式 workflow 不直接啟動 `dotnet test`、VSTest 或 xUnit；所有 repository
test execution 由 `script/test-project-runner.ps1` 統一處理 project ownership、
platform routing 與 runner selection。這是 authoritative execution boundary；
workflow 只委派 project 與 selection intent，不能自行選 test host。中央 runner
對所有 test project 使用 in-process xUnit execution。所有宣告 test platform 的 assembly
也共用 runtime execution guard，中央 runner 若退化成 VSTest 會在 assembly load 時
fail closed。Runner 在 launcher 外建立完整 argument contract，並透過一次性 pipe 將
contract hash 交給 child；assembly initializer 會比對實際 command line，不能把 full-suite
authorization 重用於 class subset。任何 started child 在 setup、execution 或 capture
失敗後都由同一 process owner bounded terminate，cleanup failure 與原 failure 一併保留。
MSBuild test target 無條件拒絕 VSTest；兩者都只是 defense-in-depth
guard，不是可由呼叫者提供 property 的 authorization credential。
完整 repository suite 與 required project gate 的 workflow step 必須使用結構化的
`.github/actions/test-solution` / `.github/actions/test-project` boundary；任意 `run:`
command 或 expression 不能取代 accepted test gate。Action 只能將 inputs 映射給
`script/invoke-ci-test-action.ps1`；這個可執行 boundary 負責參數驗證、中央 runner
委派與 result validation，並由跨平台 behavioral/mutation tests 防止 action 假綠。
Central runner 仍獨占 runtime authorization 與 result validation。Recovery 先將
`script/test-project-runner.ps1` 固定為 bootstrap
trust root，再由 `Get-DownKyiTestRunnerTrustInputs` 宣告 dependency closure；provider
變更/失敗、空清單或遺失的 declared input 都必須中止 recovery。
需要證明實際 selection 的 security gate 必須使用共享 TRX
validator，透過 test definition/result 關係確認預期 class 確實有 passed result，且
runner success 不得與 failed report outcome 矛盾。

`DKYI1001` compiler analyzer 以 compilation-resolved method symbol 禁止非 process
owner 呼叫 `SqliteConnection.ClearAllPools`。它分析並回報 generated code，且必須在
repository 支援的 Debug 與 Release compilation 都執行；語法、alias、global using
與 preprocessor 形式不構成例外，caller source 也不得 suppress 該 ownership error。
唯一 process-level allowlist owner 是
`DownKyi.SystemBenchmarks`。

重要文件：

- `module-boundary-ratchets.md`
- `assembly-lifecycle-stability.md`
- `assembly-lifecycle-owners.json`
- `review-invariant-policy.md`
- `review-invariant-corpus.json`
- `test-runner-policy.json`
- `../maintenance.md`
- `../operations/verification-and-rollback.md`

測試不得讀取使用者真實 settings、cookie、下載 DB 或 aria2 session。網路 contract tests 使用 fixture 或 loopback server。

Reviewer/Codex finding 必須先依 `review-invariant-policy.md` 做 root-cause
investigation，再按根因合併成永久 invariant。PR CI 執行 deterministic
failure/contract coverage；重型 race、process、GC、real-binary 與系統性
平台證據保留在 Main/rehearsal。

```powershell
pwsh ./script/test-review-invariants.ps1 `
  -Configuration Release `
  -NoRestore `
  -NoBuild
```
