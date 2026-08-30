# DownKyi Core

<div align="center">

[![GitHub Repo stars](https://img.shields.io/github/stars/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/stargazers)
[![GitHub forks](https://img.shields.io/github/forks/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/network)
[![GitHub issues](https://img.shields.io/github/issues/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/issues)
[![LICENSE](https://img.shields.io/github/license/crazysmile-PhD/downkyicore)](LICENSE)

</div>

DownKyi Core 是基於哔哩下载姬 Windows 版與 Avalonia 的跨平台 B 站影片下載工具。

## 下載

[![GitHub release](https://img.shields.io/github/v/release/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)
[![GitHub Release Date](https://img.shields.io/github/release-date/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/crazysmile-PhD/downkyicore/total)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)

請從 [latest release](https://github.com/crazysmile-PhD/downkyicore/releases/latest)
選擇符合目前作業系統與架構的套件。版本特定變更見
[CHANGELOG.md](CHANGELOG.md)。

## 功能

- 解析影片、合集、番劇、課程、收藏、歷史記錄與稍後再看等入口。
- 下載音訊、影片、封面、彈幕、一般字幕與 AI 字幕。
- 支援內建下載器與 aria2，並保留可安全續傳的任務狀態。
- 使用 FFmpeg 合併與驗證媒體；硬體編碼不可用時保留軟體 fallback。
- 匯出經過脫敏的診斷日誌。
- 將使用者資料放在作業系統的應用程式資料目錄，而不是安裝目錄。

## 資料目錄

預設資料根目錄：

- Windows：`%APPDATA%\DownKyi`
- macOS：`~/Library/Application Support/DownKyi`
- Linux：`$XDG_CONFIG_HOME/DownKyi`；未設定時通常是 `~/.config/DownKyi`

常用子目錄包括 `Media`、`Logs`、`Storage`、`Config`、`Cache` 與 `Aria`。

- `DOWNKYI_DATA_DIR` 可指定完整資料根目錄。
- `DOWNKYI_PORTABLE=1`，或程式目錄中的 portable marker，可啟用便攜模式。

資料路徑的 executable authority 是
[ApplicationDataPaths.cs](DownKyi.Core/Storage/ApplicationDataPaths.cs)；相容性
承諾與 migration 邊界見 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 診斷

關於頁面可開啟日誌目錄或匯出診斷資料。匯出流程會遮蔽 Cookie、token、
帳號識別與完整本機路徑。下載器、媒體處理或結束程序失敗都應留下可觀察、
但不包含敏感資料的原因。

## 開發者入口

修改前先讀 [AGENTS.md](AGENTS.md)，再依工作範圍開啟：

- [ARCHITECTURE.md](ARCHITECTURE.md)：architecture intent、dependency invariant
  與 compatibility commitment。
- [docs/ai-knowledge-graph.md](docs/ai-knowledge-graph.md)：topic-to-authority
  locator，不是完整 repository inventory。
- [docs/operations/verification-and-rollback.md](docs/operations/verification-and-rollback.md)：
  canonical build、test、release 與 rollback procedure。
- [docs/maintenance.md](docs/maintenance.md)：dependency 與 external binary 維護。
- [docs/testing/README.md](docs/testing/README.md)：test policy 與 machine-readable
  owners。

目前版本、SDK、package versions、test projects、平台 ownership、CI matrix 與
release package matrix 都直接查詢 repository metadata 或 workflow；README 不複製
這些 inventory。

## 免責聲明

1. 本軟體只提供影片解析，不提供資源上傳或伺服器儲存服務。
2. 本軟體解析的內容來自 B 站；著作權歸原作者所有。
3. 內容提供者與上傳者應對其提供的內容負責。
4. 本軟體僅供學習交流；未經原作者授權不得作其他用途。
5. 使用本軟體產生的著作權或其他法律問題由使用者自行承擔。
