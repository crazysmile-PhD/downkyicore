# Verification And Rollback

## 快速狀態

```powershell
git status --short --branch
git rev-parse HEAD
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath artifacts/architecture/module-boundary-audit.json
```

Audit JSON 記錄 commit SHA、source metrics 與目前 boundary markers，供 Agent 或 reviewer 看到真實狀態。

## 日常驗證

先用 `script/test-project.ps1` 驗證受影響的行為。提交程式或測試變更前，
在同一工作樹依序執行：

```powershell
dotnet restore ./DownKyi.sln
dotnet build ./DownKyi.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild -ProductOnly
git diff --check
```

Analyzer 和 code-style 設定由 `Directory.Build.props` 擁有；workflow 不重複傳入
相同設定。純 Markdown／文件變更只需檢查 diff 與連結；workflow 變更另跑：

```powershell
$workflowFiles = Get-ChildItem ./.github/workflows -Filter *.yml |
  Select-Object -ExpandProperty FullName
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -- $workflowFiles
```

格式整理使用 `dotnet format`，不再設置每張 PR 的全 solution formatting job。
CI runner、process cleanup 或 recorder 的具體修改可用
`script/test-ci-infrastructure.ps1` 在受影響平台驗證。

## CI 執行範圍

| 事件／變更 | 自動執行 |
| --- | --- |
| PR 程式、測試、script 或設定變更 | 一個 Linux restore、Release build 與 applicable product tests |
| PR 純 Markdown／文件變更 | 同一個 required job 檢查 changed paths 後成功結束，不啟動 .NET |
| PR `.github/**` 變更 | 同一個 job 額外執行 actionlint |
| push 到 main | 不重跑 PR CI |
| CodeQL／NuGet audit | 每週及手動執行；不作每張 PR 的 required check |
| `v*` tag／手動 Build | 三平台完整測試、六 RID source build、package validation；只有 tag 發布 |

Required check 僅為 `Build and test (ubuntu-x64)`。文件 PR 仍建立同一個
job，因此不會因 workflow path filter 而留下永久 Pending。

PR CI 以 GitHub 的 merge commit 相對第一個 parent 的 diff 判定整張 PR 的變更。
新 head 取消該 PR 舊的 CI；失敗時才上傳 TRX 與 bounded flight-recorder evidence。
Windows／macOS 的行為問題延至發布驗收發現，必要時可以提早手動執行 Build。

不再維護獨立的 CI infrastructure、macOS ad-hoc package、Issue 句型驗證或
FFmpeg source auto-merge workflow。FFmpeg／appimagetool 更新 PR 不自動啟動完整打包；
FFmpeg source 由維護者檢查後合併。發布 Build 仍包含原生平台與 package 驗證。
日常 `-ProductOnly` 也排除 runner／forensics 自測，以及 package、tag 與 release
API 回讀的三組測試；完整發布測試不加此參數。

新增 CI 前先說明目前已發生的問題、最小可重現案例、為何既有檢查不足與每次執行成本。
被刪除的規則不保留停用副本；將來出現新證據時再決定是否新增。

Repository 測試經 `script/test-project.ps1` 或 `script/test-solution.ps1` 進入
`DownKyi.CentralTestRunner`，保留 platform selection、TRX validation 與 exit result。
正常 PASS 不保留 flight-recorder evidence；失敗證據 locator 見
`docs/testing/README.md`。

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
推 tag 後由 `build.yml` 完成一次正式發布驗收；不要求先手動跑相同的完整 Build。
手動 dispatch 只用於需要提早檢查 package 的工作。同一次 run 的 `Pre-release artifacts` matrix
會下載完整 artifact inventory，不重新 build，重算每個 package sidecar，並在
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
