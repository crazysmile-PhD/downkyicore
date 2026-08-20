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

每個 `*.Tests.csproj` 必須明確宣告 `DownKyiTestPlatform`，值只能是
`cross-platform`、`windows`、`linux` 或 `macos`。MSBuild、
`script/test-solution.ps1` 與 Assembly Lifecycle runner 都會先驗證完整
project inventory，再只執行 cross-platform 與目前 runner 所有的平台項目；
遺漏、條件式或無效 ownership 會 fail closed。OS-specific behavioral tests
必須位於對應 platform project；Architecture tests 只驗證 ownership、workflow
wiring 與靜態 release invariant，不模擬另一個作業系統。

目前 audit 已將 macOS signing 行為移至 `DownKyi.MacOS.Tests`，並將 Windows
Job Object 與 startup secret handle 行為移至 `DownKyi.Windows.Tests`。
TLS runtime、certificate storage、path comparison 與 Unix file-mode assertions
仍是刻意跨平台執行的 platform-adaptive coverage，不是整個 test 的 OS skip。

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
