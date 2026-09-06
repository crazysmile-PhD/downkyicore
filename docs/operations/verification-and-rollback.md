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
dotnet format ./DownKyi.sln --no-restore --verify-no-changes
pwsh ./script/audit-code-metrics.ps1 -Configuration Release -NoRestore
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

`audit-code-metrics.ps1` 是獨立的 CA1506 架構稽核。finding 本身不阻擋，
但 build、SARIF 解析、分類輸入或 Markdown/JSON 報告產生失敗會阻擋。
目前分類與輸出契約見 `../testing/code-metrics-audit.md`。

Repository 測試只能經由 `script/test-project.ps1` 或
`script/test-solution.ps1` 進入 `DownKyi.CentralTestRunner`。Runner 會套用
project/platform allowlist、canonical invocation、必要的 in-process xUnit
routing、TRX validation 與 exit result。

正常 PASS 不保留 flight-recorder evidence。FAIL、timeout 或 abnormal cleanup
會在 `artifacts/test-flight-recorder` 保存 slice/root process identity、事件、
bounded stdout/stderr 與一次 best-effort final process snapshot；child 未被
觀察到不代表已證明不存在。詳細 locator 見 `docs/testing/README.md`。

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
資料排除。macOS artifact 另需確認 x64 與 arm64 final app 均已完成簽章並
通過 `codesign --verify --deep --strict`；缺少 Apple credentials 時使用 ad-hoc
簽章，Developer ID、notarization、stapling、Gatekeeper 與 signed-DMG 驗證會
跳過，產物不得宣稱具備這些信任屬性。具備完整 Apple credentials 時才要求
上述額外步驟全部通過。任何 final app bundle 完整性失敗仍必須 fail closed。

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
