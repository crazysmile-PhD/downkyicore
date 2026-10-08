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

## CI 觸發與 Expected Skip 政策

此節是 GitHub Actions 預期跳過的權威政策。workflow 的 `on`、`paths`、
`if`、`needs` 與 matrix 是實際執行條件；本節解釋其風險理由，變更條件時應
同步更新既有 architecture regression。`skipped` 只表示某一層條件未滿足，
不能單獨證明漏驗。先確認事件、PR 的整組 changed paths、job 依賴結果，
再判斷該類修改需要的驗證是否真的缺席。多種檔案同時變更時，取各列驗證
的聯集；workflow 的 path filter 以整個 diff 判斷，並非逐檔隔離 job。

所有 PR 均執行 `Strict PR CI` 的 Format check 與三平台 Build and test、
`CodeQL` 的 Analyze C#、`Dependency policy`。這六個 job 是目前 `main`
branch protection 的 required checks，均無 PR path filter 或 job-level `if`。
`Strict PR CI` 的 Format check 還做本地 upload/download action round trip。
`Build`、macOS ad-hoc package、aria2 TLS 與 CI infrastructure 是額外的
選擇性驗證，不列為全 PR required checks；把 path-filtered workflow 的
job 設成 required 會讓沒有觸發該 workflow 的 PR 永久 Pending。反過來，
不能以 required checks 已綠燈取代本列真正需要的選擇性 job；應查看其
實際結果。GitHub branch protection 的設定在 repository 外，修改設定後
須重新核對這個相容性前提。

| PR 修改類型／代表路徑 | 除所有 PR 基線外，應執行的驗證 | 預期跳過與理由 |
| --- | --- | --- |
| 一般文件、`AGENTS.md`、`docs/maintenance.md` | 無額外 job；基線檢查文件及現有測試 | Build 發布鏈、macOS package、aria2 TLS、CI infrastructure：沒有對應 binary／package／runner 風險。`docs/testing/test-runner-policy.json` 是例外，須跑 CI infrastructure。 |
| 一般應用程式 `.cs`／XAML，及一般測試 `.cs` | 基線三平台 build/test 與 CodeQL；測試修改由其所屬 test project 執行 | Build 發布鏈與 package matrix：程式變更本身不要求每張 PR 產生九種發布包；推 tag 前仍須 exact-head 手動 Build。aria2 TLS、CI infrastructure 只在各自 path filter 命中時執行。 |
| runner／測試基礎設施 owner、`quality.yml`、`ci-infrastructure.yml`、相關測試 | `CI infrastructure tests` 三平台 matrix | 發布包 job：runner 風險由專屬 slices 驗證；其餘一般測試仍由基線執行。 |
| aria2 installer、TLS runtime／測試與共用下載器 | `aria2 TLS security` 六 RID matrix；`script/aria2.sh` 另跑 macOS ad-hoc package | Build 發布鏈通常跳過；共用下載器會觸發 Build tooling，但只有 production FFmpeg manifest 變更才進入 package chain。 |
| `script/assets/external-assets.json` | Build 的 tooling、manifest detection、遠端資產 preflight、三平台 release gate、package/ARM64 validation；另跑 aria2 TLS、macOS ad-hoc package | `Pre-release artifacts` 與 Publish release 在 PR 預期跳過：下載後全量驗收留給非 PR 的 exact-head Build。 |
| FFmpeg 實作／工具、`install-appimagetool.ps1`、`build.yml`、`update-ffmpeg-assets.yml` | Build 的 tooling、manifest detection、PR FFmpeg manifest gate；`ffmpeg.sh`／`ffmpeg-assets.py` 另跑 macOS ad-hoc package | manifest 未變時 production preflight 應跳過，`release-gate` 因 `needs` 跳過，後續 package chain 亦跳過；tooling job 跑 FFmpeg Python tests 與 workflow lint，appimagetool 的實際安裝仍在 manifest／手動 Build 驗證。 |
| `script/macos/**`、macOS packaging inputs、`validate-publish-output.ps1` | macOS ad-hoc package x64／arm64；對應 package／architecture tests 仍在基線 | Build 發布鏈通常跳過；ad-hoc 驗證不宣稱 Developer ID／notarization。其他平台打包腳本（如 `script/pupnet/**`）由基線的現有針對性測試與發布前 Build 驗收，沒有通用 PR package matrix。 |
| `validate-build-run-artifacts.ps1` 等下載後發布驗證腳本 | 基線中的 `ReleaseSafetyRegressionTests` synthetic inventory／package validator fixtures、`ReleaseWorkflowArchitectureTests` wiring；發布前手動 Build 驗證真實下載包 | PR Build 發布鏈預期跳過。只把腳本加進 Build `pull_request.paths` 仍無法執行下載驗證：manifest 未變，preflight 跳過，`release-gate` 及其後全部因 `needs` 跳過。強制跑包需改變另一層政策，成本與風險必須另證。 |
| 其他 GitHub Actions 配置／本地 `.github/actions/**` | 基線 test／format（包含 action round trip）與 formal `actionlint`；若改到某個選擇性 workflow，執行該 workflow 的現有 PR path filter | 不因所有 Actions 修改而跑 Build；`build.yml` 命中 Build、`macos-adhoc-package.yml` 命中 macOS package 等。變更未納入某專屬 filter 時，需檢查基線與 focused tests 是否已覆蓋，而非把所有 workflow 全開。 |

事件與依賴：

| 事件 | 應執行 | 預期跳過／重跑條件 |
| --- | --- | --- |
| PR（目標 `main`；Build 另接受歷史 integration branch） | 六個 required checks；選擇性 workflow 依 changed paths；Build 內 FFmpeg tooling／diff detector／PR result gate 在 Build 被觸發時執行 | 沒有 manifest 變更時 Build preflight 與 release chain 跳過；PR 的 `pre-release-validation`、Publish release 永遠不執行。修正造成缺席的 path、`if` 或 `needs` 後，以新 head 重跑 PR。 |
| push 到 `main` | Strict PR CI、CodeQL；Dependency policy、aria2 TLS、CI infrastructure 仍依各自 push paths | Build 沒有一般 branch push；macOS ad-hoc 沒有 push。需要實際包時手動在 exact head 執行 Build。 |
| `workflow_dispatch` | Build（預設完整 release gate／package／六 scope 下載驗收，但不發布）、macOS ad-hoc、aria2 TLS、Dependency policy 等各自有手動入口的 workflow | Build 的 `update_ffmpeg_assets=true` 會改走 updater，刻意跳過 package chain；`bootstrap_ffmpeg_assets` 只供 updater。手動 Build 驗收須用預設 `false`。 |
| `v*` tag push | Build preflight、三平台 release gate、所有 package 與 ARM64 validation、六 scope 下載驗收、Publish release；tag/version/subject 門檻適用 | PR 專屬 FFmpeg result gate 跳過；updater 跳過。任何必要上游失敗／跳過都應阻斷下游與發布，不可視作安全綠燈。 |

真正的錯誤跳過包括：path filter 漏掉已證明必需的 owner、必需 job 的 `if`
與事件不相容、`needs` 的上游在該事件不會成功、或 branch protection
要求了 path-filtered workflow 的 check 而使 PR Pending。提議擴大 trigger
前，先列出違反的具體驗證要求、可重現的漏驗情境、現有輕量驗證為何不夠、
新增 runner 時間／資源成本，以及必要的最小修復。不能只因 `skipped` 或
增加表面覆蓋率而改成全面跨平台打包，也不能削弱 tag 發布鏈。

Repository 測試只能經由 `script/test-project.ps1` 或
`script/test-solution.ps1` 進入 `DownKyi.CentralTestRunner`。Runner 會套用
project/platform allowlist、canonical invocation、必要的 in-process xUnit
routing、TRX validation 與 exit result。

正常 PASS 不保留 flight-recorder evidence。FAIL、cancellation 或 abnormal cleanup
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
推 tag 前手動執行 `build.yml`；同一次 run 的 `Pre-release artifacts` matrix
會下載完整 artifact inventory，不重新 build，重算每個 package sidecar，並在
對應原生平台重新檢查 manifest、版本、必要 binary、Fluent theme 與使用者
資料排除。`Publish release` 也以同一驗證腳本再次整理並核對下載內容，且只在
整個 matrix 成功後執行。macOS artifact 另需確認 x64 與 arm64 final app 均已完成簽章並
通過 `codesign --verify --deep --strict`；缺少 Apple credentials 時使用 ad-hoc
簽章，Developer ID、notarization、stapling、Gatekeeper 與 signed-DMG 驗證會
跳過，產物不得宣稱具備這些信任屬性。具備完整 Apple credentials 時才要求
上述額外步驟全部通過。任何 final app bundle 完整性失敗仍必須 fail closed。

正式 tag 前及 workflow 中均執行：

```powershell
$version = (Get-Content ./version.txt -Raw).Trim()
pwsh ./script/validate-release-version.ps1 -GitRef "refs/tags/v$version"
```

這個檢查要求 tag 與 `version.txt` 完全一致；不得移動或重用既有 tag。

登入態 API audit 只能由明確授權的 operator 執行：

```powershell
pwsh ./script/audit-bilibili-authenticated-api.ps1 `
  -ConfirmAuthenticatedLive `
  -OutputPath ./artifacts/bilibili/authenticated-live.json
```

腳本只從 `~/.codex/.env` 讀取 `BILIBILI_TEST_COOKIE`，不得把值放入命令列、檔案、log、fixture、commit 或 PR。`/x/web-interface/nav` 未同時滿足 code 0 與 `isLogin=true` 時，後續 probe 必須封鎖。

## UI 與 runtime evidence

- Real Host/XAML：`UiSmokeTests`。
- Navigation history：typed navigation tests，必須驗證 instance reuse、dispose 與 history shrink。
- Download/retry：loopback fake HTTP tests，不連正式 Bilibili。
- Media output：ffprobe seek/decode integration tests。
- Logs：使用測試指定隔離目錄，檢查 redaction、flush、rotation 與 export。
- System performance：依 `../performance-baseline.md` 記錄 runtime、OS、architecture、dataset、backend 與 SHA。

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
