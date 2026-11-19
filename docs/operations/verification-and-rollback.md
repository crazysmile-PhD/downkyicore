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
`CodeQL` 的 Analyze C#、`Dependency policy`。這六個 job 對應 `main`
branch protection 的 required checks，名稱不變，均無 PR path filter 或
job-level `if`。架構測試、嚴格 analyzers、原生平台測試及 actionlint
仍在 PR 基線；不以預測失敗率選擇或刪減測試。

驗證分為三層：PR 基線、按 owner 路徑觸發的 CI infrastructure／外部來源
驗證，以及發布 Build。一般 PR 不編譯 FFmpeg、不產生發布包。
macOS 打包統一由 `build.yml` 負責；不再維護另一份 ad-hoc package workflow。
本地 upload/download action round trip 移到 CI infrastructure，修改本地
`.github/actions/**` 時執行一次，不再附加於每張 PR 的格式檢查。
Round trip 由上傳 job 成功後再啟動下載 job，覆蓋發布鏈的跨 job 交接契約；
artifact 名稱從 producer output 傳遞，重跑失敗 consumer 時不自行重算 attempt。
CI infrastructure 的三平台 slices 保留，仍使用正式 CentralTestRunner。

`Build` 與 CI infrastructure 是選擇性驗證，不列為全 PR required checks；
把 path-filtered workflow 的 job 設成 required 會讓沒有觸發該 workflow 的
PR 永久 Pending。不能以 required checks 已綠燈取代確實需要的選擇性 job。
GitHub branch protection 的設定在 repository 外，修改設定後須重新核對。

| PR 修改類型／代表路徑 | 除所有 PR 基線外，應執行的驗證 | 預期跳過與理由 |
| --- | --- | --- |
| 文件、一般 `.cs`／XAML、一般測試、`version.txt` | 無額外 workflow | 不產生九種發布包；tag Build 完成全套驗收後才發布。`docs/testing/test-runner-policy.json` 依 infrastructure filter 處理。 |
| runner／測試基礎設施 owner、`quality.yml`、`ci-infrastructure.yml`、相關測試與共用 build inputs | CI infrastructure 三平台 slices 與 action round trip | 發布包不屬於 runner 驗證；基線仍執行其餘測試。觸發路徑只維護在 infrastructure 的 PR filter。 |
| `.github/actions/**` | CI infrastructure（含單一 Ubuntu action round trip） | wrapper 修改的動態契約在此驗證；不因此啟動所有發布包。 |
| `script/assets/external-assets.json`、`script/ffmpeg-source/**`、`script/ffmpeg.ps1`、`script/ffmpeg.sh` | Build preflight、三平台 release gate、六 RID 原始碼編譯、encoder 與 package／ARM64 validation | 外部來源更新會進入自動合併流程，保留實際編譯驗證。PR 的下載後發布驗收與 Publish release 仍跳過。 |
| `script/macos/**`、`script/pupnet/**`、`validate-publish-output.ps1` 等 packaging inputs | 基線中的 package／architecture fixtures 與原生平台測試；需要真實包時在該 head 手動 Build | 完整打包集中到 tag，手動候選預演可選；打包缺陷可能延後到手動 Build 才被發現，不能將 PR 基線視為成品驗收。 |
| `validate-build-run-artifacts.ps1` 等發布驗證腳本、`build.yml` | 基線的 ReleaseSafetyRegressionTests、ReleaseWorkflowArchitectureTests 與 actionlint；tag Build 驗證真實下載包，需要提前驗收時手動 Build | 不為 workflow 配線或 validator 修改自動啟動九種發布包。 |
| 其他 GitHub Actions 配置 | 基線測試與 actionlint；若命中 infrastructure 或 Build 的 PR filter，執行對應層 | 不因所有 Actions 修改而啟動發布鏈。 |

| 事件 | 應執行 | 預期跳過／重跑條件 |
| --- | --- | --- |
| PR | 六個 required checks；選擇性 workflow 依 changed paths | 新 head 取消同一 workflow 的舊 PR run；不取消其他 PR。Build 的 pre-release-validation 與 Publish release 永遠不在 PR 執行。 |
| push 到 `main` | CodeQL（更新預設分支安全掃描基線） | 不重跑已在 PR 執行的 Strict PR CI、Dependency policy、CI infrastructure。合併結果不再自動重測；需要驗證整合 head 時手動 Strict PR CI 或 Build。應經受保護的 PR 合併。 |
| `workflow_dispatch` | Build 執行完整 release gate（含 Linux 的 format／actionlint）、所有 package、六 scope 下載驗收；Strict PR CI、CI infrastructure、Dependency policy 各可獨立手動驗證 | Build 的手動執行不發布。需要 runner 自測時手動 infrastructure；不需要為此產生包。 |
| `schedule` | CodeQL、Dependency policy 與外部來源 updater 依原排程 | 保留對新漏洞、淘汰套件與外部來源更新的偵測。 |
| `v*` tag push | Build preflight、三平台完整 release gate、所有 package／ARM64 validation、六 scope 下載驗收、Publish release | tag/version/subject 門檻全部適用；任何必要上游失敗／跳過均阻斷發布。 |

Concurrency 只取消同一 PR 的過時 run；手動與 tag run 以 `github.run_id`
建立獨立 group，不會因新的 PR、tag 或預演取消正在進行的發布。

### 成本判斷與研究依據

同時衡量等待時間（wall time）與 runner 工作量（各 job 時間總和），不把
縮短某個測試步驟等同於縮短整條 pipeline。以下是
[發布準備 PR 的 Strict PR CI](https://github.com/crazysmile-PhD/downkyicore/actions/runs/44254104039)
及[原先獨立 macOS package](https://github.com/crazysmile-PhD/downkyicore/actions/runs/44254104092) 的 2026-11-18 步驟資料；
這是改動前樣本，不是改動後效能保證。

| 工作 | job 時間 | 主要成本 |
| --- | --- | --- |
| Format check | 123 秒 | format 82 秒；artifact round trip 的上傳／下載各 1 秒 |
| 三平台 build/test | Windows 271、Ubuntu 288、macOS 308 秒 | 嚴格 build 83–144 秒、測試 99–149 秒 |
| macOS ad-hoc package | arm64 745、x64 1375 秒 | app／FFmpeg build 分別 550、1104 秒 |

移除獨立 macOS package 可在這類 PR 省下約 35.3 runner 分鐘，原本約
23 分鐘的額外等待回到約 5 分鐘基線。一般程式 PR 原本就不跑此 package，
不能宣稱它們也有同等加速。移除一次 main 的 quality 重跑，依相同樣本
約省 16.5 runner 分鐘；CodeQL 的 main 掃描仍保留。action round trip 的
移動是責任歸屬整理，不是主要效能收益。

同一 release subject 的[手動 Build](https://github.com/crazysmile-PhD/downkyicore/actions/runs/44257075416)
與 [tag Build](https://github.com/crazysmile-PhD/downkyicore/actions/runs/44261274382)
另分別耗時約 31、39 分鐘。tag run 已在 Publish release 前執行完整 gates，
因此不再要求固定先跑一次手動 Build；需要預先檢查真實包時才預演。
手動預演不會發布，tag run 也不重用其他 run 的未驗證產物。

- Elbaum、Rothermel、Penix，FSE 2014，
  [Techniques for improving regression testing in continuous integration development environments](https://research.google/pubs/techniques-for-improving-regression-testing-in-continuous-integration-development-environments/)：
  將提交前的測試選擇與提交後的驗證排序分開處理。本專案採用分層原則，
  不移植論文演算法，也不套用其效能數字。
- Jin、Servant，ICSE 2021，
  [What helped, and what did not? An Evaluation of the Strategies to Improve Continuous Integration](https://jxianhao.github.io/files/ICSE21.pdf)：
  比較 CI 成本與失敗回饋等不同指標，指出略過測試未必能省下 build 準備成本，
  且選擇策略有延後失敗觀察的代價。因此先移除重複整體執行與非日常打包，
  保留 PR 的架構與三平台測試；完整發布驗收是明確的後段邊界。

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

正式 Release 介紹直接使用 `script/read-release-notes.ps1` 讀取
`CHANGELOG.md` 中與 `version.txt` 相符的二級版本章節，保留原有中文與
Markdown，不再從 commit 訊息重新產生。發布前先執行下列命令確認內容；
缺少版本、同一版本重複或內容只有標題／註解時，流程會停止：

```powershell
pwsh ./script/read-release-notes.ps1
```

從儲存庫根目錄執行 Windows 資產腳本；腳本不可依賴目前 working
directory：

```powershell
pwsh ./script/ffmpeg.ps1 x64
```

FFmpeg 的目前來源版本由 `script/ffmpeg-source/vcpkg.json` 的 baseline 選定，
官方原始碼只以 `script/ffmpeg-source/ffmpeg-source.tar.gz` 保存目前一份，
參見 [FFmpeg source build](ffmpeg-source-build.md)。腳本從來源編譯並驗證
`ffmpeg`、`ffprobe` 與目標平台需要的 encoder；不再下載 FFmpeg 預編譯附件。
`script/assets/external-assets.json` 維護 appimagetool 的 numeric stable version 與 SHA-256；
production 與 historical build 都由 installer 導出官方版本化 URL，沒有 project-owned mirror。

交叉發布必須明確 restore 同一個目標 RID，再以 `--no-restore` publish。
Core 只保存外部 binary catalog，不得選擇平台內容或設定 SDK
`RuntimeIdentifier`。exe 專案必須從明確的 publish RID 建立 asset RID，
沒有 publish RID 時才可依本機 host 提供開發 fallback，並直接把對應
catalog 檔案加入 output/publish；自訂 RID 不得跨 ProjectReference。
正式發布以 tag 的 `build.yml` 作為單一完整驗收與發布鏈。手動 Build 是可選的
候選預演，用於需要在 tag 前檢查真實包的修改，不要求每次發布先完整打包
兩遍；已知 blocker 與必要 PR 檢查仍須在 tag 前解決。同一次 run 的
`Pre-release artifacts` matrix 下載完整 artifact inventory，不重新 build，
重算每個 package sidecar，並在
對應原生平台重新檢查 manifest、版本、必要 binary、Fluent theme 與使用者
資料排除。`Publish release` 也會先以相同驗證腳本再次整理並核對下載內容，
接著才以組裝腳本建立公開集合，且只在整個 matrix 成功後執行：

```powershell
$version = (Get-Content ./version.txt -Raw).Trim()
pwsh ./script/validate-build-run-artifacts.ps1 `
  -ArtifactDirectory ./artifacts/downloaded-release-candidate `
  -Scope Inventory `
  -OutputDirectory ./artifacts/pre-release-validation
pwsh ./script/assemble-release-assets.ps1 `
  -ArtifactsDirectory ./artifacts/downloaded-release-candidate `
  -ExpectedVersion $version `
  -OutputDirectory ./artifacts/release-assets
```

輸出必須只包含各平台 installer 與單一 `DownKyi-<version>-verification.zip`；
目前共九個 installer 加一個 ZIP，合計十個公開附件。ZIP 根目錄保存原有的
九份 `.sha256` 與九份 `publish-manifest-*.json`，檔名及內容逐位元組保留，
不合併或重新序列化 manifest。GitHub Release 不再單獨上傳這些驗證檔。正式
發布時建立 Draft Release、上傳完整集合並一次發布；發布後由
`verify-published-release.ps1` 以 publisher API 回讀精確 tag、公開附件名稱、
大小、SHA-256 digest 與 `immutable=true`，並與剛上傳的組裝目錄直接比對。
不得更新或覆寫已發布的 immutable release；若在
發布前中斷，只能先人工檢查並移除未發布 draft，再從相同 exact-head run 重跑。

macOS artifact 另需確認 x64 與 arm64 final app 均已完成簽章並
通過 `codesign --verify --deep --strict`；缺少 Apple credentials 時使用 ad-hoc
簽章，Developer ID、notarization、stapling、Gatekeeper 與 signed-DMG 驗證會
跳過，產物不得宣稱具備這些信任屬性。具備完整 Apple credentials 時才要求
上述額外步驟全部通過。任何 final app bundle 完整性失敗仍必須 fail closed。

正式 tag 前及 workflow 中均執行：

```powershell
$version = (Get-Content ./version.txt -Raw).Trim()
pwsh ./script/validate-release-version.ps1 -GitRef "refs/tags/v$version"
```

這個檢查要求 tag 與 `version.txt` 完全一致；不得移動或重用既有 tag。正式
發布後不再重複下載整組公開附件。發布 job 直接將 GitHub API 回報的附件
名稱、大小與 SHA-256 digest 對照本次已驗證的組裝輸出，因此仍會拒絕
缺件、多餘件、位元組不一致、`.internal.*` 或散落的 per-package 驗證檔。

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
- Media output：FFmpeg 真實程序整合測試，驗證程序成功、臨時輸出非空與原子提交；FFmpeg 失敗直接回傳，不再進行第二次解碼。
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
