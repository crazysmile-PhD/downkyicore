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
- `DownKyi.Linux.Tests`：Linux process group 與 native process ownership 行為。
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
platform routing 與 runner selection。PowerShell wrapper 只載入固定的 compiled
entrypoint、轉交 typed options 並傳播結果；workflow 只委派 project 與 selection
intent，不能自行選 test host。`DownKyi.CentralTestRunner` 對所有 test project
使用 in-process xUnit execution，並獨占 policy、canonical arguments、one-shot
authorization、aggregate orchestration 與 TRX semantics。它不直接啟動、等待或
終止 test child。

每次執行由中央 runner 建立完整 immutable argument contract、current-user random
named endpoint、random token 與完整 argv hash，再以同一個 caller-owned
`TransitionBudget` 將 immutable `LaunchSpec` 交給 `OwnedProcessLease`。
`OwnedProcessLease` / `SupervisorHost` 是 test child start、pre-execution ownership、
wait、terminate、reap、quiescence、streams 與 cleanup deadline 的唯一 owner；
SupervisorHost 只 transport opaque endpoint/token metadata，不驗證 test policy。
Assembly initializer 會清除 transport environment、驗證 exact token / argv hash /
frame EOF，拒絕 legacy numeric HANDLE/fd 字串與 direct execution。Full-suite
authorization 不能重用於 class subset，authorization 不能 replay。

Shared TRX validator 仍是 authoritative result boundary：process exit 0 不會覆蓋
failed TRX，zero executed tests fail closed，需要證明 selection 的 gate 必須逐一
確認 expected class 有 executed 與 passed result。Linux CI 在 project/solution
mode 分支之前只建立既有 delegated-cgroup context；直接執行 review corpus 也必須
在載入 runner 前進入相同 delegated scope。實際 containment/membership 仍由
shared process-supervision owner 完成，沒有 PID/process enumeration fallback。
`-NoBuild` 只禁止重建選定的 test target；若固定 compiled runner provider 缺席，
wrapper 必須依相同 restore policy 先補齊 provider，再把 `NoBuild=true` 原樣傳給
target，不能改走 PowerShell test-host fallback。
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
- `../exec-plans/pr-197-stage-5-central-test-runner.md`
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
