# AGENTS.md - DownKyi AI Agent Guide

本文件是 AI Agent 的儲存庫入口。內容必須描述目前可執行的架構，不得保留已移除架構的操作指引。

## 強制閱讀順序

1. 在分析或修改 DownKyi 程式碼前，必須先閱讀 `docs/ai-knowledge-graph.md`，確認受影響節點、依賴、穩定契約、風險與測試。
2. 閱讀 `ARCHITECTURE.md`，區分目前可執行拓樸與目標拓樸，不得把目標設計誤報為已完成。
3. 執行重構前閱讀 `docs/refactoring-live-plan.md`，只處理目前分組，不得拆分或合併計畫指定的 PR 範圍。
4. 涉及建置、依賴、外部 binary、分析器或發版時，再閱讀 `docs/maintenance.md` 與 `docs/operations/verification-and-rollback.md`。
5. 新增、刪除、移動或重新導向模組責任的 PR，必須同步更新知識圖譜、架構文件與即時計畫。

## 專案概況

DownKyi 是 .NET 10 與 Avalonia 12 的跨平台 Bilibili 下載器。主要技術如下：

- Microsoft Generic Host 與 `Microsoft.Extensions.DependencyInjection`：生命週期和 composition root。
- CommunityToolkit.Mvvm：binding 狀態與 `ObservableObject`。
- typed navigation/dialog contracts：UI 導航、對話框與通知，不依賴全域容器。
- SQLite3 Multiple Ciphers + `Microsoft.Data.Sqlite.Core`：下載任務、歷史與舊加密資料相容。
- Downloader / aria2：內建與 RPC 傳輸後端。
- FFmpeg/ffprobe：混流、轉碼、硬體編碼 fallback 與輸出驗證。
- xUnit v3：Domain、Application、Infrastructure、Core、Desktop、App 與架構測試。

Prism、DryIoc、EventAggregator、RegionManager、ContainerLocator、靜態 `LogManager`、Debugging Console wrapper 與 `SettingsManager` singleton 已移除。不得重新引入。

重要現況：`DownKyi.Domain`、`DownKyi.Application`、`DownKyi.Infrastructure`、`DownKyi.Desktop` 已建立，但尚未承接全部目標責任。`DownKyi.Desktop` 目前主要是 Host builder；Views、ViewModels、desktop adapters 與多數 runtime 仍在根層 `DownKyi`，HTTP/aria2/FFmpeg/logging 等相容實作仍多在 `DownKyi.Core`。修改前先執行 `script/audit-module-boundaries.ps1`，不可只依 project 名稱推斷實際 owner。

## 儲存庫結構

```text
DownKyi.sln
Directory.Build.props              全域 nullable、分析器與 warning policy
Directory.Packages.props           Central Package Management
version.txt                        版本唯一來源

src/DownKyi.Domain/                immutable domain state 與 typed results
src/DownKyi.Application/           use-case contracts、desktop contracts、lifetime
src/DownKyi.Infrastructure/        SQLite store、clock、write-behind 等 adapters
src/DownKyi.Desktop/               framework-neutral Host 建立入口

DownKyi.Core/                      Bilibili API、設定、日誌、aria2、FFmpeg 相容核心
DownKyi/                           Avalonia App、composition、views、ViewModels、runtime services
  Composition/DesktopComposition.cs
  Platform/                        Avalonia navigation/dialog/lifecycle adapters
  Services/Download/               download orchestration and transfer runtime
  Views/
  ViewModels/

tests/DownKyi.Domain.Tests/
tests/DownKyi.Application.Tests/
tests/DownKyi.Infrastructure.Tests/
tests/DownKyi.Core.Tests/
tests/DownKyi.Desktop.Tests/
tests/DownKyi.Tests/
tests/DownKyi.Architecture.Tests/

benchmarks/DownKyi.BenchmarkCases/
benchmarks/DownKyi.Benchmarks/
docs/
  design-docs/                      架構決策與深度審查
  exec-plans/                       任務書入口與執行規則
  product-specs/                    使用者行為與 release acceptance
  testing/                          測試分層與 architecture ratchets
  operations/                       驗證、診斷、發布與回滾
```

`DownKyi.Core` 與根層 `DownKyi` 仍含既有產品模型及 Bilibili API 相容面。不要僅為目錄整齊搬動它們；跨層移動必須先有測試保護資料格式、XAML binding 與外部協定。

`src/DownKyi.Desktop` 目前不是完整 Desktop boundary。目標是把 Views、ViewModels、UI projections、desktop adapters 與 lifecycle 移入該 assembly，並讓 executable 只保留最小 startup/composition。完整順序以 live plan 為準。

## 啟動與 Composition

啟動鏈如下：

```text
Program
  -> Avalonia App
  -> DownKyiHost.Create()
  -> DesktopComposition.AddDownKyiDesktop()
  -> Microsoft DI
  -> MainWindow + MainWindowViewModel
  -> AvaloniaApplicationLifecycle.StartHostAsync()
```

規則：

- `App.axaml.cs` 只管理 Avalonia 啟動、Host 接線、全域例外觀察與結束釋放。
- 所有服務與 ViewModel 註冊集中於 `DownKyi/Composition/DesktopComposition.cs`。
- 依賴一律透過建構子注入；禁止 service locator、靜態 App 服務屬性與第二個容器。
- `MainWindow` 建構時載入完整 XAML，並由 Host 注入 ViewModel、設定與生命週期。
- Host root XAML 禁止 `ViewModelLocator.AutoWireViewModel` 與 `RegionManager.RegionName`。

## UI 邊界

Application 層的 desktop contracts 位於 `src/DownKyi.Application/Desktop`。Avalonia adapters 位於 `DownKyi/Platform`：

- `IAppNavigationService` / `AvaloniaNavigationService`
- `IAppDialogService` / `AvaloniaDialogService`
- `IUserNotificationService` / `DesktopNotificationService`
- `IApplicationLifecycle` / `AvaloniaApplicationLifecycle`
- clipboard、file picker、platform launcher 與 UI dispatcher contracts

導航使用 `AppRoute`、`AppNavigationRegion`、`AppNavigationRequest` 和 `AppNavigationContext`。不得傳遞 View 名稱字串、region 名稱字串或依賴導航 framework 的 journal。

ViewModel 應只保留 binding state、command wiring、導航與 UI 投影。網路、解析、SQLite、下載建立、檔案 IO、FFmpeg 與 aria2 工作屬於 coordinator/service/runtime。

## 下載 Runtime

目前下載鏈如下：

```text
DownloadBootstrapHostedService
  -> IDownloadRuntimeFactory / DownloadRuntimeFactory
  -> DownloadOrchestrator                 bounded channel + workers + shutdown
  -> DownloadPipeline                     task workflow and media stages
     -> ITransferBackend                  Builtin or Aria2
     -> DownloadArtifactWriter            cover, subtitle, danmaku, NFO
     -> DownloadTaskStateWriter           projection persistence boundary
  -> DownloadTaskProjectionStore
  -> IDownloadTaskStore / SqliteDownloadTaskStore
```

此鏈仍有已追蹤的過渡債：orchestrator 每 500 ms 掃描 UI download collection，channel 元素仍是 `DownloadingItem`，Domain aggregate 主要由 projection store 反向重建。這些不是穩定契約，必須依 live plan 改成 `DownloadTaskId` event-driven queue 與 Domain-authoritative state flow。

穩定契約：

- 未完成任務、GID、partial file map、已完成分段 key、pause/progress 與 optimistic version 必須跨重啟保存。
- 關閉取消不能跳過 `Downloading -> WaitForDownload` 的恢復寫入。
- DURL 依 `Order` 排序且每段 key 包含 order；多段輸出必須經 ffprobe 驗證 seek/decode。
- 刪除下載中任務必須清除實體暫存與 sidecar；取消與失敗不得把空檔或錯誤頁標記為成功。
- 進度寫入走 bounded write-behind；UI 通知與 SQLite 寫入不得退回每個 byte/chunk 一次。
- `DownloadPipeline` 不得重新持有字幕 API、彈幕轉換、NFO XML 或 SQLite 例外處理實作。

## 設定、資料與隱私

- 設定入口是注入的 `ISettingsStore`；讀取 `Current` immutable snapshot，修改使用 typed `Update`，持久化使用 cancellation-aware flush。
- `SettingsStore` 必須保留既有 JSON property 名稱、schema migration、atomic replace 與 legacy DES 設定遷移。
- 路徑由 `StorageManager` 解析；測試必須用隔離目錄，禁止讀取真實 cookie、設定、下載 DB 或 aria2 session。
- SQLite schema 變更必須有版本 migration、備份、rollback 與 reopen 測試。
- 日誌使用注入的 `ILogger` 與 `ApplicationLogProvider`。不得記錄 cookie、token、完整敏感 URL、email、帳號 ID 或完整個人路徑。
- 低階 API 不得直接輸出到 terminal，也不得同時在多層重複記錄同一失敗。

## MVVM 與非同步

- ViewModel 繼承 `ViewModelBase` / `ObservableObject`，使用 `SetProperty` 或 source-generated observable properties。
- 非同步 command 使用現有 `DownKyiAsyncDelegateCommand`，command 實例應快取或在建構子建立。
- 除真正 UI event handler 外禁止 `async void`。
- ViewModel 禁止 `Task.Run`。CPU 或阻塞相容 API 只可在明確 service/infrastructure 邊界隔離。
- 不得使用 `.Result`、`.Wait()` 或 `.GetAwaiter().GetResult()`。
- 所有長工作接受並傳遞 `CancellationToken`；`OperationCanceledException` 必須保留取消語意。
- fire-and-forget 必須由 `RunFireAndForget` 或明確 observer 記錄 fault，不得丟棄 Task。
- UI collection/property 更新經 `IUiDispatcher` 或既有 UI dispatch helper；背景 service 不得直接依賴 Avalonia control。

## HTTP 與外部程序

- Bilibili HTTP 目前使用 factory 建立的 `BilibiliHttpClient`，但仍經過 static `WebClient` facade 且含同步 send/read/backoff。不得擴大此相容面；目標是注入 async `IBilibiliApiClient` 並移除 `WebClient.Configure()`。
- retry 必須迭代、有限、尊重 cancellation/backoff；耗盡後丟出明確例外，不得回傳空字串偽裝成功。
- JSON 空字串、HTML 錯誤頁與 schema failure 必須可見。
- WBI 簽名必須從 `IWbiKeyProvider` 取得目前有效金鑰；`WbiSign` 不得讀取 settings。只有 WBI request 的 `-403` 可強制刷新並重試一次。
- `data`、`result` 等可選 envelope 欄位必須保留缺失狀態；端點明確選擇契約欄位，禁止用預設空 DTO 偽裝成功。
- FFmpeg/ffprobe 與 aria2 由現有 processor/server/backend owner 啟動與釋放；禁止 shell command string 拼接。
- 硬體編碼採成功率優先：能力偵測後使用 GPU，失敗再 fallback 到軟體編碼。
- 外部 binary 版本、來源與 checksum 依 `docs/maintenance.md` 維護。

## 分析器與風格

根層 `Directory.Build.props` 預設啟用：

```xml
<EnableNETAnalyzers>true</EnableNETAnalyzers>
<AnalysisMode>All</AnalysisMode>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<CodeAnalysisTreatWarningsAsErrors>true</CodeAnalysisTreatWarningsAsErrors>
```

禁止為通過建置而新增廣域 `NoWarn`、`#pragma warning disable`、`SuppressMessage`、`GlobalSuppressions.cs`、`severity = none/silent`、`#nullable disable` 或關閉分析器。協定要求的最小範圍例外必須有理由與測試。

新 C# 使用 file-scoped namespace、nullable annotation、明確 cancellation 與最小必要註解。遵守 `.editorconfig`；不要在功能 PR 混入無關格式化。

## 建置與測試

提交前依序執行，禁止在同一工作樹平行跑 build/test：

```powershell
dotnet restore .\DownKyi.sln

dotnet build .\DownKyi.sln `
  -c Release `
  --no-restore `
  --no-incremental `
  -p:EnableNETAnalyzers=true `
  -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true `
  -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true

dotnet test .\DownKyi.sln -c Release --no-restore --no-build
dotnet format .\DownKyi.sln --no-restore --verify-no-changes
git diff --check
dotnet package list .\DownKyi.sln --vulnerable --include-transitive
dotnet package list .\DownKyi.sln --deprecated
```

關鍵永久防線：

- `UiSmokeTests.RealHostResolvesShellAndKeyViewsWithoutPrismRuntime`
- `RootViewArchitectureTests`
- `LegacyPatternArchitectureTests`
- `DownloadRuntimeArchitectureTests`
- `ModuleBoundaryBaselineTests`
- `AgentEnvironmentArchitectureTests`
- SQLite migration/resume、download shutdown recovery、DURL identity/seekability tests

每次修正行為都應新增能在舊實作上失敗的測試；不要只以 build 成功代表 runtime 正常。

## Package 與發版

- 套件使用 Central Package Management；版本只放 `Directory.Packages.props`。
- 應用版本只讀 `version.txt`，更新檢查與 GitHub tag 必須使用同一語意版本。
- PR CI 擋確定錯誤；nightly 執行跨平台整合、效能與資源報告；release gate 驗證所有平台 package、binary checksum、資料 migration 與下載回歸。
- 系統效能基準必須記錄 runtime、OS、architecture、dataset、backend 與 commit SHA，不得比較不同機器的臨時計時器數值。
