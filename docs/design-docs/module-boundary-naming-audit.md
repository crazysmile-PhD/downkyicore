# DownKyi Module Boundary And Naming Audit

Status: verified baseline
Baseline date: 2026-07-22
Baseline commit: `66dbe5161bb1683acff419c618554eb5da5c445a`
Baseline branch: `refactor/pr-30-32-release-hardening`

## 結論

附件報告指出的七類問題大多成立，但報告使用 `9be3289` 與不可追溯的對話 citation 作為基準，且遺漏了四個更高優先缺口。以目前工作樹重新量測後，專案的 build、test、analyzer 與跨平台 release gate 已相當成熟，但 Domain、Application、Infrastructure、Desktop 仍未成為實際 runtime 的主要責任邊界。

目前不得宣告重構完成，也不得發布 v1.1.0。`docs/refactoring-live-plan.md` 原本標示 complete 與實際狀態不符：最終 stacked branch 尚未進入 `main`，PR #75、#77、#79、#80 仍為 open，版本唯一來源仍是 `1.0.32`。

## 可重現基線

執行：

```powershell
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath artifacts/architecture/module-boundary-audit.json
```

輸出包含 commit SHA、project references、各 source root 的檔案/行數、邊界違規、命名 inventory、巨檔與 runtime markers。下列數據使用實體 `*.cs` 與 `*.axaml` 行數，不等同 cyclomatic complexity 或有效 LOC。

| Source root | Files | Physical lines |
|---|---:|---:|
| `DownKyi` | 280 | 41,238 |
| `DownKyi.Core` | 275 | 19,742 |
| `src/DownKyi.Domain` | 11 | 586 |
| `src/DownKyi.Application` | 17 | 427 |
| `src/DownKyi.Infrastructure` | 7 | 1,720 |
| `src/DownKyi.Desktop` | 1 | 39 |

既有 `DownKyi` 與 `DownKyi.Core` 佔上述 production source 約 95.7%。行數不能單獨證明設計錯誤，但配合 project references 與 runtime type usage，足以證明新 project boundaries 仍只承接少部分產品責任。

## 優先級總表

| Priority | Finding | Current evidence | Verdict |
|---|---|---|---|
| P0 | 發布與計畫狀態不一致 | final branch not in `main`; PR #75/#77/#79/#80 open; `version.txt=1.0.32` | confirmed |
| P1 | `DownKyi.Desktop` 是名義邊界 | 1 source file, 39 lines; Views/VMs/adapters remain in executable | confirmed, omitted by attachment |
| P1 | Domain aggregate 不是 runtime authority | 52 `DownloadingItem` references vs 15 Domain task/id references in download runtime | confirmed, omitted by attachment |
| P1 | Channel 仍輪詢 UI collection | `DispatchAsync`, 500 ms delay, `Channel<DownloadingItem>` | confirmed, omitted by attachment |
| P1 | `DownloadPipeline` 仍是 mixed-responsibility owner | 1,058 lines, UI models/resources, retry, media, persistence | confirmed |
| P1 | HTTP DI 只完成一半 | static `WebClient` facade plus sync `Send`, `ReadToEnd`, `WaitOne` | confirmed, omitted by attachment |
| P1 | Core 仍依賴 UI | 5 known files/project entries | confirmed |
| P1 | service contracts 仍依賴 presentation | 4 interfaces | confirmed, attachment undercounted |
| P1 | custom collection contract 不完整 | 5 consumers, 5 `NotImplementedException` members | confirmed, omitted by attachment |
| P2 | naming and folder taxonomy inconsistent | 9 duplicate-name groups, 5 generic names, 7 file/type mismatches | confirmed with qualifications |
| P2 | oversized owners | 18 production files above 500 physical lines | confirmed, attachment undercounted |
| P2 | logging owner too broad | `ApplicationLogProvider` 715 lines and multiple responsibilities | confirmed design risk, not proven runtime defect |
| P1 | AI knowledge environment incomplete | `ARCHITECTURE.md` and required docs taxonomy absent before this audit | confirmed |

## Finding 1: 名義上的 Desktop 邊界

`src/DownKyi.Desktop` 只有 `Composition/DownKyiHost.cs`。真正的 Views、ViewModels、navigation adapters、dialogs、lifecycle 與 UI dispatcher 仍位於 executable project `DownKyi`。`DownKyi.csproj` 直接參考 Core、Application、Desktop、Domain 與 Infrastructure。

這不是「project 很薄」本身有錯，而是專案名稱暗示了不存在的隔離。現有 `ProjectDependencyTests` 只嚴格檢查四個 `src` projects，無法阻止 executable 中的 ViewModel 同時依賴 UI、store 與 runtime implementation。

目標：讓 `DownKyi.Desktop` 真正承接桌面層，`DownKyi.exe` 只保留最小啟動與 composition root。

## Finding 2: Domain 不是下載狀態權威

`DownloadTask` aggregate 具有合法狀態轉換，但 `DownloadOrchestrator`、`DownloadPipeline` 與 backends 仍以 `DownloadingItem` 為主要資料。`DownloadTaskProjectionStore` 使用 `CreateUnfinishedTask()` 和 `DomainDownloadTask.Restore(...)` 將 legacy mutable model 反向重建成 Domain task。

目前資料流：

```text
mutable UI model -> Domain reconstruction -> SQLite
```

目標資料流：

```text
Domain task -> persistence -> Desktop projection
```

只有完成這個反轉後，Domain transition rule 才能控制 runtime，而不是只驗證持久化包裝。

## Finding 3: Channel 仍以 UI collection 輪詢入隊

`DownloadOrchestrator` 使用 bounded channel 與固定 workers 是正確方向，但 `DispatchAsync()` 每 500 ms 掃描 `_downloadLists.Downloading`，再用 `_queuedDownloads` 去重。

影響：

- 最多約 500 ms 的排程延遲。
- 掃描成本隨 UI list 增長。
- UI collection 成為下載 engine input。
- runtime 與 Desktop projection 無法分離。

目標是 `EnqueueAsync(DownloadTaskId)`，啟動時只從 store 一次性恢復 queued/interrupted tasks。

## Finding 4: DownloadPipeline 仍是 God Object

`DownloadPipeline.cs` 有 1,058 physical lines，直接操作 `DownloadingItem`、download lists、UI display state、播放地址、音訊/影片/DURL、retry、FFmpeg、artifact 與 persistence。先前抽出的 artifact/state writers 是有效改善，但不代表 pipeline 已完成拆分。

目標 stages 與契約見根層 `ARCHITECTURE.md`。File-length ratchet 只防止惡化，不可替代責任測試。

## Finding 5: Retry ownership 重複

Pipeline 的外層 retry limit 是 5；Builtin backend 又依序嘗試 primary 和 backup URLs。錯誤分類、URL refresh 與 retry budget 沒有單一 owner。

目標 policy：

| Failure | Decision |
|---|---|
| timeout / 5xx | bounded exponential backoff |
| 429 | honor `Retry-After` |
| confirmed expired URL / 403 | resolve playback once, then retry once |
| invalid media from one endpoint | next backup endpoint |
| disk / permission | fail immediately |
| cancellation | never retry |

Pipeline 或 backend 只能有一層控制 budget。另一層只能回傳 typed outcome。

## Finding 6: HTTP 抽象仍是 static/synchronous facade

`BilibiliHttpClient` 已由 `IHttpClientFactory` 建立，但 `WebClient` 保存 static client/configuration，核心請求仍使用同步 `HttpClient.Send`、`StreamReader.ReadToEnd` 與 `WaitHandle.WaitOne` backoff。

目標是注入 `IBilibiliApiClient`、`IBuvidProvider`、`IWbiKeyProvider`，使用 `SendAsync`、`ReadAsStringAsync`、`CopyToAsync` 和 `Task.Delay`。移除 static `WebClient.Configure()` 前，所有 endpoint 必須具備 deterministic fixture tests。

## Finding 7: Core 仍含 UI

已確認 baseline：

- `DownKyi.Core/DownKyi.Core.csproj`
- `DownKyi.Core/BiliApi/Login/LoginQR.cs`
- `DownKyi.Core/Utils/QRCode.cs`
- `DownKyi.Core/BiliApi/BilibiliImages.axaml`
- `DownKyi.Core/BiliApi/Zone/ZoneImages.axaml`

目標是 Core 回傳 login QR payload/descriptor，由 Desktop renderer 產生 Avalonia `Bitmap`；image resource dictionaries 移至 Desktop。

## Finding 8: Service contracts 反向依賴 presentation

已確認四個 interface files：

- `DownKyi/Services/IInfoService.cs`
- `DownKyi/Services/IFavoritesService.cs`
- `DownKyi/Services/Download/IAddToDownloadSession.cs`
- `DownKyi/Services/Download/ITransferBackend.cs`

它們的契約直接使用 `DownKyi.ViewModels` types。這些應先改為 Domain/Application DTO 或 typed transfer context，再移至 Application ports。

## Finding 9: 自製 collection 名稱與契約不一致

`ImmutableObservableCollection<T>` 是可變 `IList<T>/IList`，使用 immutable backing list，但五個非泛型介面成員會在 runtime 丟出 `NotImplementedException`。Immutable backing 不會自動提供 atomic update、UI-thread ownership 或 notification ordering。

目標：

```text
Domain task changed
  -> Desktop projector
  -> owner-only ObservableCollection
  -> ReadOnlyObservableCollection exposed to View
```

這項必須有 UI binding、collection contract、thread-affinity 與 consumer migration tests。

## Finding 10: 命名 inventory 需要分類，不可機械化全域禁止

目前偵測到 9 組跨 namespace simple-name duplicates。`ViewSeasonsSeries*` 和兩份 `VideoInputResolver` 是明確可維護性缺陷；API DTO 中的 `Subtitle`、`VideoPage` 等名稱可能是 endpoint scope 下的合理名稱，不能僅因 simple name 重複就全面禁止。

目前 generic-name baseline 有 5 項：兩個 `Utils`、兩個 `Constant`、一個 `StorageManager`。應以責任拆分和具名 owner 取代，不建議建立會禁止所有 `*Manager`、`*Helper` 的全域 analyzer。

目前 file/type mismatch inventory 有 7 項。只有 `LoginQR.cs`/`LoginQr` 與 `QRCode.cs`/`QrCode` 是明確 casing mismatch；其餘包含 multi-type aggregate file 或 intentionally named command wrapper，需逐檔決策。

附件提供的「檔名必須等於第一個型別」正規表示式會誤判 partial、`.axaml.cs`、多型別 DTO 與 interface companion records。此方案不採用。

## Finding 11: 巨檔與 logging owner

目前 18 個 production files 超過 500 physical lines。最優先的手寫 owners：

- `DownloadPipeline.cs` 1,058
- `ViewVideoViewModel.cs` 1,020
- `SqliteDownloadTaskStore.cs` 928
- `ApplicationLogProvider.cs` 715
- `ViewMySpaceViewModel.cs` 669
- `AddToDownloadService.cs` 667

`AriaClient.cs` 1,137 行屬 RPC client 類型，應先確認生成/同步來源，不可只為行數拆分。

Logging 風險成立，但「換成熟 sink」需要 ADR 與跨平台/隱私 benchmark。專案特有 redaction 必須發生在磁碟、recent buffer 與 diagnostic export 之前。不能只因 class 很大就直接引入新套件。

## 附件報告的修正

| Attachment statement | Audit correction |
|---|---|
| 代表證據不是全 repo 統計 | 已加入可重現全 repo inventory script |
| Core UI 代表證據 4 | 實際 baseline 5，含兩個 `.axaml` resources |
| service/presentation 代表證據 3 | 實際 interface baseline 4 |
| duplicate names 4 | 實際跨 namespace group 9，但不是全部都應禁止 |
| `DownloadPipeline` 934 LOC / 1,058 lines | 使用可重現 physical line count 1,058；不混用未定義 LOC |
| 先加入會紅的 architecture tests | 不採用；改用 subset/max ratchet，CI 維持綠色 |
| global duplicate-name ban | 不採用；會誤判 protocol DTO |
| first-type/file-name test | 不採用；現況會誤判大量 legitimate files |
| 工期 35-55 人日 | 屬估算，不是程式碼證據；任務書改以完成條件管理 |

## 新增的可執行防線

`ModuleBoundaryBaselineTests` 目前保護：

1. Core UI dependencies 不可新增。
2. service contract presentation dependencies 不可新增。
3. duplicate full-name sets 不可擴大。
4. generic type-name baseline 不可擴大。
5. file/type mismatch baseline 不可擴大。
6. 500 行以上檔案不可新增或增長。
7. Domain-to-legacy reconstruction 不可離開 projection owner。
8. UI collection polling 不可擴散。
9. static/sync HTTP debt 不可擴散。
10. custom mutable collection 不可增加 consumers 或 unsupported members。

這些測試是過渡 ratchet。每移除一項債務，應同步刪除對應 baseline entry；不得把 baseline 當成永久例外清單。

## 完成判定

模組邊界與命名重構只有在以下證據齊全時才算完成：

- `DownKyi.Desktop` 實際擁有 Views、ViewModels、UI projections 與 adapters。
- executable 只保留最小 startup/composition。
- Domain task 是下載狀態唯一 authority。
- orchestrator 不掃描 UI collection。
- pipeline stages 可獨立測試且不依賴 presentation types。
- retry budget 有單一 owner。
- Bilibili HTTP 全 async、injected、無 static facade。
- Core 無 Avalonia package/type/resource。
- Application/service contracts 無 ViewModel types。
- custom collection 已由標準 ownership pattern 取代。
- 命名 baseline entries 清零或只保留有 ADR 的 protocol exceptions。
- 所有 target projects、root executable 與 legacy compatibility area 都受 architecture tests 約束。
- stacked refactor 已整合到 `main`，PR #75/#77/#79/#80 已由新架構實作取代。
- v1.1.0 release gate 全數通過。
