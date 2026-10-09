# Historical version converter

這個工具只處理已存在的 `archive/v1.0.0`～`archive/v1.0.24`。它不建立或
移動標籤、不改寫歷史、不使用正式 `v*` 發布流程，也不把臨時相容性調整提交
回舊版原始碼。

每個版本的成功結果包含：

- GitHub 從封存標籤自動提供的 source archives；
- `DownKyi-<version>-historical-win-x64.zip`；
- `DownKyi-<version>-historical-osx-x64.dmg`；
- `DownKyi-<version>-historical-linux-x64.AppImage`；
- 每個安裝包各自的 `.sha256` 與 `.manifest.json`。

Release 一律保持 Draft、不是 Latest，名稱為
`v<version> — Historical Rebuild`。說明與 manifest 會明確區分「依歷史原始碼
重建的產物」和「原作者當年的官方產物」。macOS 只使用 ad-hoc 簽章，不會
宣稱已做 Developer ID 簽署或 notarization。

## 一鍵執行

在 GitHub Actions 選擇 **Historical Rebuild** → **Run workflow**，保留預設值
`version=all`、`platform=all`、`rebuild_existing=false`，即可處理全部 25 個
版本與三個平台。等價的 GitHub CLI 指令是：

```powershell
gh workflow run historical-rebuild.yml `
  -f version=all `
  -f platform=all `
  -f rebuild_existing=false
```

只重跑單一失敗平台時，例如 1.0.7 的 macOS：

```powershell
gh workflow run historical-rebuild.yml `
  -f version=1.0.7 `
  -f platform=macos-x64 `
  -f rebuild_existing=true
```

`rebuild_existing=false` 會在該版本的安裝包、checksum 與 manifest 三者均已存在
時跳過該平台。工作流程不會建立重複 Release；若同一標籤已經有非 Draft
Release，會 fail closed 而不修改它。

## 執行與權限邊界

1. `Ensure-HistoricalDraftReleases.ps1` 只建立缺少的 Draft Release。
2. `New-HistoricalBuildPlan.ps1` 依現有 assets 建立缺項 matrix。
3. `Prepare-HistoricalRuntimeAssets.ps1` 依目前受控的
   `script/assets/external-assets.json` 下載並驗證 aria2、FFmpeg 與
   appimagetool；不執行舊版未固定版本或未驗 checksum 的下載腳本。
4. `Invoke-HistoricalBuild.ps1` 在臨時 detached worktree 建置指定 tag，注入上述
   已驗證依賴、檢查版本與封裝內容並做基本啟動測試，最後移除 worktree。
5. `Sync-HistoricalDraftReleases.ps1` 在 Linux 上只重算 checksum、讀 manifest、
   上傳檔案和更新 Draft 說明；它不執行歷史程式碼。

工作流程預設只有 `contents: read`。唯一取得 `contents: write` 的
`ensure-drafts` 與 `publish-drafts` 工作都在 Linux 執行，且不執行歷史版本；
實際編譯舊碼的三平台工作沒有寫入 Release 的權限，也不會取得 Apple 發布憑證。

啟動測試會先對封裝內真正的 aria2、FFmpeg、ffprobe 執行無網路的版本探測；
GUI 則從已解壓的安裝包副本啟動，並只在該副本以不開網路的 aria2 stub 取代
舊 helper。這避免舊版 aria2 RPC 設定在 runner 對外監聽；上傳的安裝包仍保留
checksum 已驗證的真正 runtime binary。此隔離方式會寫入每份 manifest。

任何單項失敗都會留下 `result-<version>-<platform>.json`，其他 matrix 項目仍會
繼續。同步階段把錯誤摘要寫入對應 Draft Release，缺件時整體 workflow 最後會
失敗，避免把未驗證的包當成成功。

## 本機驗證

不建立 Release 的靜態驗證：

```powershell
pwsh ./script/historical-rebuild/Test-HistoricalRebuild.ps1
```

本機端到端建置需要所在平台、x64、.NET 6 SDK，以及先執行 runtime asset
準備腳本。所有輸出與 worktree 都必須放在臨時目錄；正式批次建議使用上述
GitHub Actions 入口。
