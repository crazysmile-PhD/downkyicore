# Maintenance Flight Manual

用途：維護工作先看這裡；找到 owner 後，只讀該卡與連結文件。

口訣：**找主、守界、取證、停手**。

1. 找主：誰擁有狀態與 transition？
2. 守界：哪些相容性／安全 invariant 不能破壞？
3. 取證：哪一個 regression 與 gate 能證明結果？
4. 停手：出現第二個 owner 或第二個可獨立交付結果就停止。

若文件與 current code／可重現測試不一致，以 code 和 evidence 為準，並在同一
PR 修正文件。

## 十秒路由

| 任務 | Authoritative owner | 最少證據 | 詳細入口 |
| --- | --- | --- | --- |
| NuGet dependency | `Directory.Packages.props` | restore、strict build、tests、package audit | 本文件「依賴」 |
| Test／CI failure | `DownKyi.CentralTestRunner` + OS test project | TRX + failure recorder | `docs/testing/README.md` |
| Download persistence | Domain task + Application service + SQLite store | transition／migration tests | 本文件「下載資料」 |
| Transfer／media | coordinator + selected backend + media validator | focused runtime regression | 本文件「傳輸與媒體」 |
| Known runtime gap | quick cards（locator only） | current-main repro + owner confirmation | `docs/exec-plans/v1.1.1-runtime-hardening.md` |
| Settings | `ISettingsStore`／`SettingsSchemaMigrator` | settings + architecture + Host tests | 本文件「Settings」 |
| Logging | `ApplicationLogProvider` + Infrastructure logging owners | provider stress + Host tests | `docs/design-docs/logging-ownership-sink-adr.md` |
| Desktop／DI／theme | Desktop composition + design tokens | architecture + XAML + packaged smoke | `ARCHITECTURE.md` |
| Bilibili／WBI | API adapter + `IWbiKeyProvider` + fixtures | contract fixture + inventory gate | `docs/operations/bilibili-api-audit.md` |
| Analyzer | strict build config + analyzer inventory | clean strict build + inventory | `docs/analyzer-baseline.md` |
| aria2／FFmpeg binary | `script/assets/external-assets.json` + installer scripts | digest + six-RID gates | `docs/operations/aria2-security.md` |
| Release | `version.txt` + release workflow | exact-head release gates | `docs/operations/verification-and-rollback.md` |

## 共用起飛檢查

- [ ] 一句話寫出 Core、Done、Stop。
- [ ] 從 current `main` 重現問題或驗證需求。
- [ ] 找到唯一 owner；不建立平行 registry、retry、cleanup、persistence 或 DI。
- [ ] 先跑 focused proof，再跑風險相稱的正式 gate。
- [ ] 比較 diff；刪除不能解釋為 Done 所必需的修改。

立即停手：需要第二個 owner、新 dependency、新 workflow、跨 owner call edge，或只能
靠 retry／timeout／catch 讓結果看起來成功。

## 標準品質閘門

Focused test 先用 `script/test-project.ps1`。完整正式閘門：

```powershell
dotnet restore ./DownKyi.sln
dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental `
  -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true `
  -p:EnableNETAnalyzers=true `
  -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true
pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild
dotnet format ./DownKyi.sln --verify-no-changes --no-restore
dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive
dotnet package list --project ./DownKyi.sln --deprecated
git diff --check
```

`CompileUsingReferenceAssemblies=false` 是跨平台 hosted-build 穩定性政策。沒有 exact-SDK
cross-platform stress proof，不得移除。

## 依賴卡

- 只在 `Directory.Packages.props` 改 managed package 版本。
- 不把 dependency update 與非必要 refactor 混在同一 PR。
- 跑標準品質閘門；deprecated report 需要人工判讀。
- Stop：dependency change 若迫使產品語義改變，拆成獨立 scope。

## Test／CI 卡

- 每個 `*.Tests.csproj` 明列 `DownKyiTestPlatforms`：`Windows;Linux;macOS` 的明確子集。
- Native behavior 放在對應 OS test project；正式入口只走 CentralTestRunner scripts。
- PASS 刪除 flight recorder；FAIL 保存 bounded output、cleanup 與 best-effort snapshot。
- Snapshot 不是完整 descendant proof；沒有 failure-window owner evidence 時寫
  `Root cause not proven.`。
- Resource contention 依 `docs/testing/targeted-resource-forensics.md`；ETW 只可對窄目標
  預先啟用，不能 blanket-enable。
- CodeQL 保持 explicit `manual` build；不得為消除提示改成 buildless mode。
- Stop：新 CI failure 若不同 owner／不同 evidence，列 Pending，不為綠燈擴 scope。

## 下載資料卡

Owner：

- Active task：Domain `DownloadTask` → `DownloadTaskApplicationService` →
  `IDownloadTaskStore`。
- Completed history：`DownloadHistoryService` → `IDownloadHistoryStore`。
- Active → history：只由 `IDownloadCompletionStore` 在一個 transaction 完成。
- SQL／storage JSON：只由 `SqliteDownloadTaskStore` 擁有；UI projection 不擁有 SQL。

守界：

- 不從 mutable UI 或 lossy history 重建 active Domain task。
- 短連線、WAL、optimistic version、transaction；不要 process-wide DB lock／connection。
- Migration：先 backup，再單一 transaction DDL，成功後才改 `user_version`，並有 rollback test。
- Active/history collision 由 active owner 優先；history quarantine 不得改名成 active evidence。
- Malformed row 個別 quarantine；不得截斷後續 keyset pages，也不得記錄 raw JSON、完整
  path、Cookie 或 URL。
- Completion 先完成 durable active→history transaction；之後的 staging cleanup 是
  best effort。Lossy history 不保存 cleanup locator，restart 不保證補做。
- 啟動載入全部 unfinished tasks 與最新 100 筆 history；其餘使用 keyset paging。
- State 先 durable commit，再更新 UI；高頻 progress 可 bounded/coalesced。
- 更新 `SQLite3MC.PCLRaw.bundle` 必跑 `LegacySqlCipherCompatibilityTests`。

## 傳輸與媒體卡

- Queue：bounded workers；入口只傳 `DownloadTaskId`，不得輪詢 `ObservableCollection`。
- Admission：`DownloadTaskAdmissionService` 唯一擁有 output reservation。
- Backend：built-in／aria2 共用 key、resume、integrity、persistence 與 coordinator policy。
- Success：backend 完成後仍須通過 shared DownKyi final-file integrity。
- Pause／shutdown：保留 GID、partial map、completed keys、progress 與 version。
- Delete：先 persist `Canceled`，再 stop backend、刪產物／sidecars、最後刪 row；cleanup
  失敗不得假裝成功。
- DURL：初始與刷新 manifest 先拒絕 duplicate `Order` 及無可用地址 segment，再排序。
  不自行增加 positive、gap、contiguity 或 must-start-at-1 規則。
- Mux／concat：same-directory temp；不覆蓋 foreign destination；失敗保留有效 source。
- 只有 real fail-on-error decode 證明 source 無效時，才撤銷 completed key／identity／file。
- FFmpeg：只由 `FfmpegProcessRunner` 擁有 concurrency、timeout、cancellation 與 process tree。
- HTTP：401／403／schema failure 不 retry；429 bounded `Retry-After`；cancellation 不 retry。

Loopback cancellation test 必須先同步到 server 已收到 request；固定 sleep／timeout 不是同步。

## Settings 卡

- 讀：`ISettingsStore.Current` immutable snapshot；寫：typed `Update`。
- Correlated fields 用一次 `Update`；operation 開始後不得讀動態設定改變其語義。
- Schema 一次升一版；保留既有 JSON names，除非有相容 migration。
- Malformed file 先移到唯一 `.invalid-*`；newer schema file 必須 byte-for-byte 不動。
- 寫入：debounce → single async gate → temp UTF-8 JSON → parse → flush → atomic replace。
- Shutdown await `FlushAsync`／必要時 `DisposeAsync`；DES 僅可讀 legacy，禁止新寫入。
- 證據：`SettingsStoreTests`、`SettingsArchitectureTests`、Host smoke、strict build。

## Logging 卡

- 入口只注入 `ILogger<T>`；禁止 static `LogManager`、Console 與第二套 log queue/writer。
- `ApplicationLogProvider` 是唯一 MEL adapter／redaction boundary；實作在 Infrastructure。
- Redaction 必須早於 NLog 與 recent buffer；禁止 Cookie、token、account、email、完整私人路徑。
- Queue／recent buffer 保持 bounded；logging 不得阻塞 download 或 UI thread。
- Shutdown await `FlushAsync` + `DisposeAsync`；第一個 persistence failure 必須傳回 caller。
- Export：flush 後讀 persisted files、再次 redaction、跳過 malformed 並計數。
- 預設：UTC day、32 MiB rotation、7-day retention、512 MiB safety cap。
- 證據：`ApplicationLogProviderTests` stress、Host smoke、strict build。

## Desktop／Host 卡

- 依賴方向：Domain ← Application ← Infrastructure／Desktop；Infrastructure 不 reference Desktop。
- `DownKyi` 只組 concrete product registrations；全產品只有一個 Microsoft DI container。
- 禁止 Prism、DryIoc、service locator、global App services 與第二 composition root。
- `DisableDefaults=true`；新增 config provider 不得改變既有 data/settings/login/aria2 paths。
- Long-running operation 使用 linked scope：caller cancellation local，Host stop cancel all。
- Theme 只用 Fluent + Fluent DataGrid；token 在 `DesignTokens.axaml`。
- 保留 keyboard focus、high-DPI、localization 與大型列表 virtualization。
- 證據：architecture tests、Host XAML smoke、Windows packaged startup、CI platform matrix。

## Bilibili／WBI 卡

- `IWbiKeyProvider` 唯一擁有 key validity；一次 shared refresh，不被單一 waiter cancellation 取消。
- `WbiSign` 是純 protocol function；caller 傳 keys + timestamp。
- 只有 signed request 回 `-403` 可 force refresh 並 retry 一次；第二次直接傳回原錯誤。
- Public parsing 不依賴 login／home-page timing；partial nav 不得清掉已驗證 keys。
- Envelope：video=`data`、bangumi v2=`result.video_info`、cheese=`data`；缺失／空 payload
  是 typed failure。
- Endpoint／envelope 變更：同 PR 更新 API inventory + deterministic fixture。
- Live audit 必須顯式授權；authenticated audit 後跑 `script/scan-secrets.ps1`。

## Analyzer 卡

- 禁止 project-wide `NoWarn`、analyzer exclusion、`#nullable disable`、silent severity 或
  為過 build 新增 suppression。
- 修復順序：security/correctness → async/lifecycle → performance → public API → style。
- 改 field／property／collection／name 前，檢查 serialization、SQLite、XAML、reflection、protocol。
- Blocking rules 由 strict build 擁有；CA1501／CA1506 只在 advisory audit 報告。
- Inventory 用 `script/analyzer-inventory.ps1`；CSV 是 file/line authority，Markdown 是摘要。
- UI state await 用 `ConfigureAwait(true)`；reusable Core/background 用 `false`。
- Resource owner 必須有明確 `IDisposable`／`IAsyncDisposable`；fire-and-forget 必須觀察 fault。

唯一預先核准的 source-local `CA5351`：

| Location | 只允許用途 | Removal gate |
| --- | --- | --- |
| `DownKyi.Core/BiliApi/Sign/WbiSign.cs` | Bilibili WBI protocol MD5 | Bilibili 取代 WBI |
| `DownKyi.Core/Utils/Encryptor/LegacySettingsDecryptor.cs` | read-only legacy migration | 有 telemetry、recovery guidance 的 migration-window 決策 |

不得把這兩個 suppression 擴到 password、integrity 或其他 trust decision。

## External Binary 卡

- URL／checksum 唯一 owner：`script/assets/external-assets.json`；只用 immutable tag／URL。
- Installer 必須從 repository root 與 `script/` 都能執行。
- aria2 provenance 記錄 official base、DownKyi source/patch/build commit、archive/binary digest。
- Runtime 在 process start 前驗 sidecar digest，再透過 RPC 驗 required feature。
- Packaged local aria2：ephemeral loopback port、fresh 256-bit secret、restricted config、
  `--stop-with-process`；Windows 再加 kill-on-close Job Object。
- Credential header 只給 exact HTTPS `bilibili.com` host／subdomain；禁止 downgrade 與
  credential-bearing cross-origin redirect。
- Remote aria2 不由 App start/stop；non-loopback 必須 HTTPS 且需要
  `downkyi-secure-redirect-v2`。Generic aria2／Motrix 不受支援。

更新 checklist：

- [ ] 固定 source/base/build commits，mechanically regenerate canonical patch。
- [ ] 驗 patch digest、`git apply --check`、applied tree equality、`git diff --check`。
- [ ] 建六個 RID，記錄 archive + binary SHA-256。
- [ ] 從 repo root 跑 installer 並驗 sidecar。
- [ ] 六 RID `aria2-tls-security` 全過並檢查 sanitized reports。
- [ ] 驗 FFmpeg／ffprobe 與適用平台 hardware encoder；GPU 缺失仍可 CPU fallback。

詳細 supply-chain 與 residual risk：`docs/operations/aria2-security.md`、
`docs/operations/ffmpeg-asset-mirroring.md`。

## Release 卡

- [ ] `version.txt` 與 `v<version>` 完全一致。
- [ ] 在 exact release commit 手動跑 build workflow；所有 OS release/package gates 通過。
- [ ] Publish manifest 含 DownKyi、aria2、FFmpeg、ffprobe、版本與 SHA-256。
- [ ] macOS 二選一：完整 Developer ID/notarization/stapling/Gatekeeper；或明確標示的
  ad-hoc + strict bundle/DMG/copy/launch 驗證。不得宣稱 ad-hoc 是 trusted distribution。
- [ ] Signing 是最後一步；sign 後不得再改 bundle content／permission。
- [ ] 驗證完成 DMG 內的 exact app，不只驗中間 signing command。
- [ ] 跑標準品質閘門，review README + CHANGELOG。
- [ ] 先 push `main`，再 push tag；發布後核對 packages、`.sha256` 與 manifests。

`script/validate-publish-output.ps1` 是共同 package-content gate；禁止以單一平台的
file-exists check 取代。

## 手動 Smoke 卡

- [ ] 視窗關閉後 process exit；可重新開啟。
- [ ] BV、AV、bangumi、cheese 可解析。
- [ ] 單項、多 P、全選可加入下載；取消 directory picker 不新增 task。
- [ ] Pause → close → reopen 後 resume，不從零開始。
- [ ] 刪除 active task 會移除 media 與 `.aria2`／`.download` sidecars。
- [ ] Subtitle SRT time code 正確。
- [ ] Diagnostic export 不含私人路徑、Cookie、token、敏感 URL。

## 固定名稱

- Language resource：`src/DownKyi.Desktop/Languages/Default.axaml`。
- FFmpeg namespace：`DownKyi.Core.FFmpeg`。
- 禁止歷史拼法 `Languanges` 與 source-directory casing `FFMpeg`。
