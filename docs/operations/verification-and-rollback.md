# Verification And Rollback

## 快速狀態

```powershell
git status --short --branch
git rev-parse HEAD
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath artifacts/architecture/module-boundary-audit.json
```

Audit JSON 記錄 commit SHA、source metrics 與目前 boundary markers，供 Agent 或 reviewer 看到真實狀態。

## 嚴格驗證

依序執行，不要在同一工作樹平行跑 build/test：

```powershell
dotnet restore ./DownKyi.sln

pwsh ./script/validate-release-version.ps1

dotnet build ./DownKyi.sln `
  -c Release `
  --no-restore `
  --no-incremental `
  -p:EnableNETAnalyzers=true `
  -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true `
  -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true `
  -p:UseSharedCompilation=false

pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild
pwsh ./script/audit-lifecycle-ownership.ps1 `
  -OutputDirectory ./artifacts/assembly-lifecycle/ownership
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release `
  -Iterations 5 `
  -NoBuild `
  -ValidateForensics `
  -ResultsDirectory ./artifacts/assembly-lifecycle/verification
dotnet format ./DownKyi.sln --no-restore --verify-no-changes
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath ./artifacts/architecture/module-boundary-audit.json
$workflowFiles = Get-ChildItem ./.github/workflows -Filter *.yml | `
  Select-Object -ExpandProperty FullName
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -- $workflowFiles
git diff --check
dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive
dotnet package list --project ./DownKyi.sln --deprecated --include-transitive
pwsh ./script/scan-secrets.ps1
```

`scan-secrets.ps1` 使用 Gitleaks 掃描目前 tracked 與尚未追蹤、但未被 `.gitignore` 排除的候選提交檔。固定驗證版本為 Gitleaks `8.30.1`；Windows x64 release zip 必須先依官方 `gitleaks_8.30.1_checksums.txt` 驗證 SHA-256，再解壓到 `.tools/gitleaks/bin/`。`.gitleaks.toml` 只允許公開 WBI 測試 fixture 與精確的 Avalonia brush resource 行，不得加入整個目錄或一般測試檔的寬鬆排除。

Lifecycle ownership audit 與 5 次全 test-assembly gate 是同一套嚴格
Verification 的必要步驟，不是選用診斷工具。每個 assembly 必須通過
load、assembly-info、discovery、execution、assembly teardown 與 process
exit；報告必須保留 stdout/stderr 污染、退出碼、P50/P95/P99/max、殘留
子程序與逾時取證。

## Release Rehearsal

正式 rehearsal 使用 `Rehearsal` profile，每個 test assembly 執行 100
次，超過 50 次最低發布門檻：

```powershell
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release `
  -Profile Rehearsal `
  -NoBuild `
  -ValidateForensics `
  -ResultsDirectory ./artifacts/assembly-lifecycle/release
```

`assembly-lifecycle-report.json`、ownership report、raw output 與 timeout
evidence 必須保存為 workflow artifact。單次重跑成功不能取代失敗 owner
的根因、teardown 修正與完整 rehearsal。

## 外部 Binary 與跨 RID 發布

從儲存庫根目錄執行 Windows 資產腳本；腳本不可依賴目前 working
directory：

```powershell
pwsh ./script/aria2.ps1 x64
pwsh ./script/ffmpeg.ps1 x64
```

URL 與 SHA-256 只在 `script/assets/external-assets.json` 維護。更新前以
publisher release API 交叉核對 immutable tag、asset name、size 與 digest；
禁止改成 mutable `latest`、使用 `curl --insecure` 或略過 checksum。驗證
`ffmpeg`、`ffprobe`、aria2 非空，並檢查目標平台需要的硬體 encoder。

交叉發布必須明確 restore 同一個目標 RID，再以 `--no-restore` publish。
Core 只保存外部 binary catalog，不得選擇平台內容或設定 SDK
`RuntimeIdentifier`。exe 專案必須從明確的 publish RID 建立 asset RID，
沒有 publish RID 時才可依本機 host 提供開發 fallback，並直接把對應
catalog 檔案加入 output/publish；自訂 RID 不得跨 ProjectReference。
推 tag 前手動執行 `build.yml`，下載每個 artifact，重算 package
sidecar，並檢查 manifest、版本、必要 binary、Fluent theme 與使用者
資料排除。

歷史 rehearsal `30431043860` 證明 v1.1.0 candidate 的三個 release
gate、九個 package job、sidecar、manifest 與實際套件內容正確；它不是
正式發布證據。後續 tag workflow 暴露偶發 Windows test-host 前台執行緒，
因此 v1.1.0 draft 已撤回，標籤保持不可變。

修正後 main run `30450175286` 是 lifecycle 根因修正的 50 輪證據：
七個 assembly 共 2,102 phase results，零失敗、零缺失 slow evidence、
零 marker read error；teardown 最大 7 ms，OS process-exit 最大 187 ms。
14 個超过五秒的 execution phase 均保存取证。v1.1.1 仍必须在最终版本
commit 上完成 `Rehearsal` 100 轮与所有跨平台 package job，才能建立 tag。

正式 tag 前及 workflow 中均執行：

```powershell
pwsh ./script/validate-release-version.ps1 -GitRef refs/tags/v1.1.1
```

這個檢查要求 tag 與 `version.txt` 完全一致；不得移動或重用既有 tag。

登入態 API audit 只能由明確授權的 operator 執行：

```powershell
pwsh ./script/audit-bilibili-authenticated-api.ps1 `
  -ConfirmAuthenticatedLive `
  -OutputPath ./docs/operations/bilibili-authenticated-api-audit.json
```

腳本只從 `~/.codex/.env` 讀取 `BILIBILI_TEST_COOKIE`，不得把值放入命令列、檔案、log、fixture、commit 或 PR。`/x/web-interface/nav` 未同時滿足 code 0 與 `isLogin=true` 時，後續 probe 必須封鎖。

## UI 與 runtime evidence

- Real Host/XAML：`UiSmokeTests`。
- Navigation history：typed navigation tests，必須驗證 instance reuse、dispose 與 history shrink。
- Download/retry：loopback fake HTTP tests，不連正式 Bilibili。
- Media output：ffprobe seek/decode integration tests。
- Logs：使用測試指定隔離目錄，檢查 redaction、flush、rotation 與 export。
- System performance：依 `performance-baseline.md` 記錄 runtime、OS、architecture、dataset、backend 與 SHA。

## 回滾

一般 PR 使用非破壞性 revert：

```powershell
git revert <commit-sha>
```

不得用 `git reset --hard` 或覆蓋使用者工作樹。

資料 migration PR 必須在合併前提供：

1. 舊 schema fixture。
2. migration 後 reopen 測試。
3. 備份位置。
4. rollback 或向前修復步驟。
5. 未完成下載與 resume state 驗證。

XAML/rename PR 回滾時應 revert 整個 rename commit，避免只還原 class 而留下 resource URI、DI 或 route references。
