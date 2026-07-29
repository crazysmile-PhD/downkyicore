# DownKyi Module Boundary And Naming Audit

Status: maintained verified audit
Last verified: 2026-07-29
Verification base: `bb082a775f7e306bbee577b3c998c58d600915ef` plus the Gate 9 aria-client working tree
Verification branch: `refactor/aria-client-provenance`

## 結論

附件報告指出的七類問題大多成立，但原始證據已被後續重構取代。以目前工作樹重新量測後，Desktop 已成為實際 UI owner、Core 已 headless、下載佇列、HTTP 與 logging 邊界也已收斂；剩餘主要缺口是 media execution context 仍讀取一個 UI projection，以及 aria2、FFmpeg 與 filesystem 的最終 Infrastructure ownership。

目前仍不得宣告整體重構完成或發布 v1.1.0：Gate 9 logging 與 naming 已分別透過 PR #94、#95 整合；large-owner 工作仍在分支逐項收斂，最終 stacked branch 也尚未進入 `main`。版本唯一來源仍是 `1.0.32`。

## 可重現基線

執行：

```powershell
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath artifacts/architecture/module-boundary-audit.json
```

輸出包含 commit SHA、project references、各 source root 的檔案/行數、邊界違規、命名 inventory、巨檔與 runtime markers。下列數據使用實體 `*.cs` 與 `*.axaml` 行數，不等同 cyclomatic complexity 或有效 LOC。

| Source root | Files | Physical lines |
|---|---:|---:|
| `DownKyi` | 1 | 14 |
| `DownKyi.Core` | 278 | 18,034 |
| `src/DownKyi.Domain` | 11 | 681 |
| `src/DownKyi.Application` | 30 | 1,143 |
| `src/DownKyi.Infrastructure` | 27 | 3,760 |
| `src/DownKyi.Desktop` | 346 | 44,090 |

`DownKyi` executable 已降為單一 14 行 bootstrap；Desktop 是最大的產品 owner。行數不能單獨證明設計品質，因此後續仍以 project references、runtime type usage 與 architecture tests 判定責任邊界。

## 優先級總表

| Priority | Finding | Current evidence | Verdict |
|---|---|---|---|
| P0 | 發布與計畫狀態不一致 | stacked release-hardening branch not in `main`; `version.txt=1.0.32` | confirmed release blocker |
| resolved | Desktop ownership | 317 files; executable is one 14-line bootstrap | completed by Gate 8 |
| P1 | Domain aggregate 不是 runtime authority | durable commands and queue identity now use Domain tasks; execution context still exposes one UI projection | durable authority resolved, projection leak remains |
| P1 | Channel 仍輪詢 UI collection | direct `DownloadTaskId` admission; no dispatcher polling or collection-membership scheduling | resolved by Gate 5 |
| P1 | `DownloadPipeline` 仍是 mixed-responsibility owner | typed six-stage sequence below 150 lines; presenter/projector isolated | resolved by Gate 6 stage extraction |
| resolved | HTTP ownership | injected async Application ports plus Infrastructure transport | completed in Gate 7 |
| resolved | Core headless boundary | 0 Avalonia/QRCoder/XAML owners | completed by Gate 8 |
| resolved | service contracts 依賴 ViewModel | 0 interfaces | completed by Gate 8 |
| resolved | custom collection contract | 0 custom collection references; standard read-only wrappers | completed by Gate 8 |
| resolved | naming and folder taxonomy inconsistent | 4 endpoint/role-scoped duplicate groups, 0 generic names, 0 file/type mismatches | completed in Gate 9 naming branch |
| resolved | oversized owners | 0 production files above 500 physical lines | aria RPC provenance classified and owner split protected by protocol tests |
| resolved | logging owner too broad | contracts in Application; 268-line provider plus dedicated Infrastructure sink/buffer/retention/export owners | completed by Gate 9 PR #94 |
| P1 | AI knowledge environment incomplete | required root/docs structure and reproducible audit scripts now exist | resolved |

## Resolved Finding 1: Desktop 實際 ownership

Gate 8 已把 Avalonia App、Views、ViewModels、Presentation models、navigation/dialog adapters、platform services、resources、lifecycle 與 desktop runtime 移入 `src/DownKyi.Desktop`。`DownKyi` 只含 `Program.cs` 且只參考 Desktop。

Host/XAML smoke 直接從 Desktop assembly 建立 App、MainWindow 與關鍵 ViewModels；architecture ratchet 禁止 executable 再取得 runtime、UI 或 package ownership。

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

Resolution (2026-07-26): Gate 5 replaced this compatibility path with direct `DownloadTaskId` admission for new, resumed, and persisted startup tasks. `DispatchAsync`, the 500 ms delay, `_queuedDownloads`, `Channel<DownloadingItem>`, and UI collection membership checks were removed. The original finding below is retained as historical rationale.

`DownloadOrchestrator` 使用 bounded channel 與固定 workers 是正確方向，但 `DispatchAsync()` 每 500 ms 掃描 `_downloadLists.Downloading`，再用 `_queuedDownloads` 去重。

影響：

- 最多約 500 ms 的排程延遲。
- 掃描成本隨 UI list 增長。
- UI collection 成為下載 engine input。
- runtime 與 Desktop projection 無法分離。

目標是 `EnqueueAsync(DownloadTaskId)`，啟動時只從 store 一次性恢復 queued/interrupted tasks。

## Finding 4: DownloadPipeline 仍是 God Object

Resolution (2026-07-26): Gate 6 extracted a typed execution context and six ordered stages. `DownloadPipeline` is now below 150 lines, localized activity and completion projection have dedicated owners, and its oversized-file/collection-consumer allowlist entries were removed. Retry ownership remains Finding 5 and is intentionally handled in the next PR.

`DownloadPipeline.cs` 有 1,058 physical lines，直接操作 `DownloadingItem`、download lists、UI display state、播放地址、音訊/影片/DURL、retry、FFmpeg、artifact 與 persistence。先前抽出的 artifact/state writers 是有效改善，但不代表 pipeline 已完成拆分。

目標 stages 與契約見根層 `ARCHITECTURE.md`。File-length ratchet 只防止惡化，不可替代責任測試。

## Finding 5: Retry ownership 重複

Resolution (2026-07-26, pending integration): `DownloadTransferCoordinator` now owns one five-attempt budget. `DownloadMediaStage` invokes it once per stream/segment, each backend receives exactly one URL, Downloader uses `MaxTryAgainOnFailure=0`, aria2 uses `max-tries=1`, `retry-wait=0`, `always-resume=false` and `max-resume-failure-tries=0`, and every aria RPC client call makes one physical request. Typed policy decisions cover transient network/5xx, 429, expired address/403, rejected resume state, invalid media, disk, permanent failure and cancellation. Retryable partial files remain resumable; rejected resume state gets one centralized cleanup retry; confirmed invalid media is removed before a backup is attempted. RPC-layer aria2 failures preserve the latest GID, while terminal task failure or explicit task-not-found clears stale identity.

Implemented policy:

| Failure | Decision |
|---|---|
| timeout / 5xx | bounded exponential backoff |
| 429 | honor `Retry-After` |
| confirmed expired URL / 403 | resolve playback once, then retry once |
| invalid media from one endpoint | next backup endpoint |
| disk / permission | fail immediately |
| cancellation | never retry |

The architecture test `TransferRetryHasOneTypedBudgetOwner` rejects a reintroduced stage retry loop, multi-URL backend call, Downloader retry, or aria2 retry. Deterministic coordinator tests prove that primary and backup addresses share one budget and playback refresh occurs at most once.

## Resolved Finding 6: HTTP ownership

Gate 7 已建立 `IBilibiliApiClient`、`IBuvidProvider` 與 `IBilibiliCookieProvider` ports，並由 Infrastructure 使用 `IHttpClientFactory`、`SendAsync`、async content/stream copy 與 cancellation-aware `Task.Delay` 實作。所有 production endpoint 已改為注入式 async 呼叫，static `WebClient`、`BilibiliHttpClient` 及全域 `Configure()` 已刪除。

Architecture tests 現在禁止重新加入 static client、同步 send/read 與 blocking backoff；Infrastructure tests 固定 retry、取消、stream ownership、partial-file cleanup、buvid single-flight 與 Host registration 契約。

## Resolved Finding 7: Core headless boundary

Core 現在只保留 login URL/status HTTP contracts。`ILoginQrCodeRenderer` 與 QRCoder/Avalonia bitmap 實作位於 Desktop，兩份 image resource dictionaries 也已移至 `src/DownKyi.Desktop/Resources/Bilibili`。Core package graph 不再包含 Avalonia 或 QRCoder，architecture test 對 Core UI/QR dependency 採零容忍而非 baseline。

## Resolved Finding 8: Service contracts 不再依賴 ViewModel

原本三個 interface files 直接引用 `DownKyi.ViewModels`：

- `src/DownKyi.Desktop/Services/IInfoService.cs`
- `src/DownKyi.Desktop/Services/IFavoritesService.cs`
- `src/DownKyi.Desktop/Services/Download/IAddToDownloadSession.cs`

相關 projection types 已移到 `DownKyi.Presentation`，interface 對 `DownKyi.ViewModels` 的引用由 3 降為 0。這些 interfaces 目前是 Desktop-internal workflow contracts；日後若移到 Application，必須先替換為 framework-neutral records，不能把 Presentation types 一併上移。

## Resolved Finding 9: 標準 collection ownership

`ImmutableObservableCollection<T>` 已刪除。`DownloadListState` 私有持有 `RangeObservableCollection<T>`，只向 ViewModel/View 公開穩定的 `ReadOnlyObservableCollection<T>`；startup、admission、completion、delete、replace 與 sort 都經 owner methods 執行。

目前資料流：

```text
Domain task changed
  -> Desktop projector
  -> owner-only ObservableCollection
  -> ReadOnlyObservableCollection exposed to View
```

collection contract 測試確認外部 mutation 會被拒絕，Host/XAML smoke 確認 binding 未退化，architecture test 則禁止自製集合或 service-to-ViewModel contract 回歸。

## Resolved Finding 10: 命名 inventory 已分類並收斂

Gate 9 將跨 namespace simple-name duplicates 由 9 組降為 4 組。`ViewSeasonsSeries*` 已分成 detail 與 user-space list owner；Desktop 的 adapter 已正名為 `PlayStreamTypeResolver`，Application 保留唯一 `VideoInputResolver`。剩餘 `BangumiType`、`FavoritesMedia`、`Subtitle` 與 `VideoPage` 都由 endpoint DTO 或明確的 Presentation/ASS role namespace 限定，architecture baseline 固定其完整型別集合，不允許增加新同名 owner。

Generic-name baseline 已由 5 項降為 0：播放品質、ASS formatting、application-data paths/storage 與 video-page projection 都使用責任名稱。測試仍掃描 `Constant`、`Utils` 與 `StorageManager`，但不再保留任何 allowlist entry。

File/type mismatch baseline 已由 4 項降為 0。Bilibili JSON DTO 與 NFO XML DTO 只拆到同名檔案，沒有改動 CLR type、`JsonProperty`、XML element/attribute 或 wire shape；async command 只修正檔名。`Languages` resource URI 與 `DownKyi.Core.FFmpeg` casing 另由跨平台 architecture test 固定。

附件提供的「檔名必須等於第一個型別」正規表示式會誤判 partial、`.axaml.cs`、多型別 DTO 與 interface companion records。此方案不採用。

## Finding 11: 巨檔 owners

2026-07-29 的實際 boundary audit 已有 0 個 production file 超過 500 physical lines；oversized baseline 為空。`DownloadPipeline`、`DownloadTaskProjectionStore`、`AddToDownloadService`、`ViewMyFavoritesViewModel`、`ViewPublicationViewModel`、`SqliteDownloadTaskStore`、`SettingsManager.Network`、`ViewNetworkViewModel`、`ViewVideoViewModel`、`ViewMySpaceViewModel`、`ViewUserSpaceViewModel`、`ViewMyBangumiFollowViewModel`、`ParseEntrance`、`CustomPagerViewModel`、`ViewNetwork.axaml`、`ViewVideoDetail.axaml`、`AriaClient.cs` 與原 715 行 `ApplicationLogProvider` 已從 allowlist 移除。SQLite Store 從 928 行降為 447 行，交易/初始化協調、Domain row mapping、讀取/quarantine 與 SQL writes 已分成具名 owner，既有 schema/migration/resume tests 保持不變。Settings 的一般 network/downloader/proxy owner 為 319 行，aria RPC/runtime owner 為 355 行；44 個既有 public compatibility methods 的方法級內容相同，且仍讀寫同一個 `ApplicationSettings.Network` schema。Network settings ViewModel 現分為 384 行 navigation/general-command owner、275 行 aria-command owner 與 292 行 binding-state owner；28 個方法與 25 個 command properties 保持等價，XAML binding 名稱未變。Network settings XAML 現為 21 行 ordered composition shell、152 行 general、108 行 built-in、274 行 bundled aria2 與 77 行 external aria2 view；token inventory 與實際 child-view smoke 固定所有 binding、名稱、資源與 command parameters。Video-detail XAML 現為 46 行 ordered composition shell、74 行 toolbar、168 行 summary、247 行 section/page selection 與 66 行 actions view；token inventory、same-namescope selection、application-owned DataGrid base theme 與完整 Host child-tree smoke 固定其 binding、資源、behavior 與 DataGrid 契約。Video settings ViewModel 現分為 451 行 navigation/playback/transcoding owner、353 行 directory/content/filename-command owner 與 248 行 binding-state owner；27 個方法、54 個 public members 與 56 個 private fields 保持等價，XAML binding 名稱與同一個 injected `ISettingsStore` ownership 未變。My-space ViewModel 現分為 408 行 navigation/profile workflow owner 與 265 行 service-free binding-state owner；12 個方法、36 個 public members 與 41 個 private fields 保持等價，typed back navigation、cancellation、settings 與 XAML bindings 未變。Add-to-download 現為 275 行 session coordinator、114 行 duplicate policy、206 行 stateless draft factory與 85 行 optional metadata builder；16 個 owner/tag/cancellation tests 固定完成紀錄、設定 snapshot、檔名、內容旗標與 queue admission 契約。User-space ViewModel 現為 412 行 typed-navigation/load/projection workflow owner、161 行 service-free binding-state owner與既有 27 行 favorite-folder owner；20 個 XAML properties 的名稱與生命週期責任由 architecture test 固定。Bangumi-follow ViewModel 現為 435 行 pager/navigation/load/download workflow owner與 103 行 service-free binding-state owner；12 個 XAML properties、pager event ownership、typed back navigation、cancellation、batch projection 與 download coordinator 邊界由 architecture test 固定。輸入解析現由 8 個責任 partial 擁有，所有檔案低於 120 行；確定性矩陣固定 AV/BV、番劇、課程、收藏夾、使用者空間與投稿清單契約，並拒絕偽造 `space.bilibili.com` host。Pager 現為 103 行 change-veto owner、110 行 XAML state、51 行 command owner與 41 行純 layout value owner；無參數按鈕不再被 `RequiredParameterCommand` 靜默丟棄，建構子也會保留要求的目前頁。Aria RPC client 已確認為 2020 年加入 DownKyi 的手動維護協定程式碼，不是 generated/vendor sync output；現拆成 107 行 transport core、333 行 download control、329 行 status/URI、134 行 options、181 行 lifecycle 與 65 行 system methods，完整公開 RPC inventory 與 method/token wire contract 由 deterministic test 固定。

Logging 風險已依 ADR 收斂並由 PR #94 整合：Application 只保留 contracts，Infrastructure 使用私有 NLog 6.1.4 `LogFactory`，並拆出 recent buffer、retention、exporter 與 redactor。專案 redaction 在 NLog、recent buffer、磁碟與 export 之前完成；module audit 對 Core implementation 與 Infrastructure 外的 NLog consumer 採零容忍。

## 附件報告的修正

| Attachment statement | Audit correction |
|---|---|
| 代表證據不是全 repo 統計 | 已加入可重現全 repo inventory script |
| Core UI 代表證據 4 | 已移除全部 5 項；目前 Core UI/QR dependency 為 0 |
| service/presentation 代表證據 3 | 已由 3 收斂為 0，且 architecture test 採零容忍 |
| duplicate names 4 | 收斂後仍為 4 組，但均有 endpoint/role namespace，完整型別集合受 ratchet 約束 |
| `DownloadPipeline` 934 LOC / 1,058 lines | 使用可重現 physical line count 1,058；不混用未定義 LOC |
| 先加入會紅的 architecture tests | 不採用；改用 subset/max ratchet，CI 維持綠色 |
| global duplicate-name ban | 不採用；會誤判 protocol DTO |
| first-type/file-name test | 不採用；現況會誤判大量 legitimate files |
| 工期 35-55 人日 | 屬估算，不是程式碼證據；任務書改以完成條件管理 |

## 新增的可執行防線

`ModuleBoundaryBaselineTests` 目前保護：

1. Core 必須維持 0 個 UI、Avalonia、QRCoder 或 `.axaml` dependencies。
2. service contract 對 ViewModel types 的依賴必須維持 0。
3. duplicate full-name sets 不可擴大。
4. generic type-name baseline 不可擴大。
5. file/type mismatch baseline 不可擴大。
6. 500 行以上檔案不可新增或增長。
7. Domain-to-legacy reconstruction 不可離開 projection owner。
8. UI collection polling 不可擴散。
9. static/sync HTTP debt 不可擴散。
10. 已刪除的 custom mutable collection 不得返回；下載清單只能公開標準唯讀 wrapper。
11. 一般 network 與 aria RPC/runtime settings owners 必須維持分離，且兩個檔案都受 500 行上限約束。
12. Network settings 的 navigation/general commands、aria commands 與 binding state 必須維持分離，每個 partial 都受 500 行上限約束。
13. Video settings 的 navigation/playback/transcoding commands、directory/content/filename commands 與 binding state 必須維持分離，每個 partial 都受 500 行上限約束，且只有 main owner 可接收 `ISettingsStore`。
14. My-space 的 navigation/profile workflow 與 binding state 必須維持分離，兩個 partial 都受 500 行上限約束，且 state owner 不得取得 coordinator、settings 或 cancellation ownership。
15. aria2 RPC transport、download control、status/URI、options、lifecycle 與 `system.*` owners 必須維持分離；所有 partial 受 500 行上限約束，公開方法 inventory 與 wire method/token 契約不得漂移。

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
