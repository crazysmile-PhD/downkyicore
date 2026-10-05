# Maintenance Flight Manual

用途：維護工作先看這裡；依問題線索找到 owner 後，只讀該卡與連結文件。

口訣：**找主、守界、取證、停手**。

1. 找主：誰擁有狀態與 transition？
2. 守界：哪些相容性／安全 invariant 不能破壞？
3. 取證：哪一個 regression 與 gate 能證明結果？
4. 停手：出現第二個 owner 或第二個可獨立交付結果就停止。

若文件與 current code／可重現測試不一致，以 code 和 evidence 為準，並在同一
PR 修正文件。

## 十秒路由

| 問題／觸發詞 | Authoritative owner | 最少證據 | 操作卡／詳情 |
| --- | --- | --- | --- |
| NuGet、package、SDK、Python、version、restore | `Directory.Packages.props`／`global.json`／`.python-version` | restore、strict build、tests、package audit | [依賴卡](#dependency) |
| CI、timeout、TRX、zero tests、cleanup | `DownKyi.CentralTestRunner` + OS test project | TRX + failure recorder | [Test／CI 卡](#test-ci) |
| SQLite、migration、history、persistence | Domain task + Application service + SQLite store | transition／migration tests | [下載資料卡](#download-persistence) |
| queue、retry、resume、media selection、aria2、FFmpeg | selection／media contract + coordinator + backend | focused runtime regression | [傳輸與媒體卡](#transfer-media) |
| 已知 runtime gap、恢復中斷工作 | GitHub Project item／linked Issue | current-main repro + owner confirmation | [DownKyiCore 工作項目](https://github.com/users/crazysmile-PhD/projects/2) |
| settings、schema、invalid file、flush | `ISettingsStore`／`SettingsSchemaMigrator` | settings + architecture + Host tests | [Settings 卡](#settings) |
| logging、redaction、export、retention | `ApplicationLogProvider` + Infrastructure logging owners | provider stress + Host tests | [Logging 卡](#logging) |
| Desktop、DI、Host、theme、XAML | Desktop composition + design tokens | architecture + XAML + packaged smoke | [Desktop／Host 卡](#desktop-host) |
| Bilibili、WBI、endpoint、envelope | API adapter + `IWbiKeyProvider` + fixtures | contract fixture + inventory gate | [Bilibili／WBI 卡](#bilibili-wbi) |
| analyzer、warning、suppression、nullable | strict build config + analyzer inventory | clean strict build + inventory | [Analyzer 卡](#analyzer) |
| asset、checksum、aria2／FFmpeg binary | `script/assets/external-assets.json` + installer scripts | digest + six-RID gates | [External Binary 卡](#external-binary) |
| release、tag、exact head、rollback | `version.txt` + release workflow | exact-head release gates | [Release 卡](#release) |

## 共用起飛檢查

- [ ] 一句話寫出 Core、Done、Stop。
- [ ] 從 current `main` 重現問題或驗證需求。
- [ ] 找到唯一 owner；不建立平行 registry、retry、cleanup、persistence 或 DI。
- [ ] 先跑 focused proof，再跑風險相稱的正式 gate。
- [ ] 比較 diff；刪除不能解釋為 Done 所必需的修改。

立即停手：需要第二個 owner、新 dependency、新 workflow、跨 owner call edge，或只能
靠 retry／timeout／catch 讓結果看起來成功。

## 標準品質閘門

迭代時先用 `script/test-project.ps1` 跑 focused test。完整正式命令、執行順序、
證據記錄與回滾方式只由
[Verification And Rollback](operations/verification-and-rollback.md) 維護；所有步驟在同一
工作樹依序執行。

`CompileUsingReferenceAssemblies=false` 是跨平台 hosted-build 穩定性政策。沒有 exact-SDK
cross-platform stress proof，不得移除。

## 未來維護模式

- 改動 stable owner、invariant 或 minimum proof 的 PR，必須在同一 PR 更新既有卡片與必要的路由觸發詞；沒有 drift 就不改文件。
- 卡片固定使用 `Use when / Owner / Invariant / Do / Proof / Stop / Details`；同一 owner 優先改既有卡，不在文件尾端追加平行說明。
- 只有出現新的 authoritative owner 與可獨立驗證邊界才新增卡片；否則視為原卡片的細化。
- 卡片只保存穩定決策。版本、數量、SHA、endpoint／test 清單與完整命令應 `LINK`、`QUERY` 或 `GENERATE`，不得手抄副本。
- `Details` 直達 authoritative owner；`Proof` 只列證據類型或 focused test，不複製正式命令。同步 `main` 後，依 merged production／test paths 重查受影響卡片。

<a id="dependency"></a>

## 依賴卡

- **Use when**：NuGet、SDK、Python、version、restore、vulnerability、deprecated。
- **Owner**：managed package version 只在 `Directory.Packages.props`；.NET SDK 只在 `global.json`；Python 只在 `.python-version`。
- **Invariant**：workflow 只引用 toolchain owner file，不重複版本值；`Dependency policy` 在每個 PR 解析兩個 toolchain owner，無效內容 fail closed，restore 與 vulnerable／deprecated audit 不因 path filter 跳過；Dependabot 每日 UTC 00:00 分別檢查 NuGet、.NET SDK 與 GitHub Actions，預設每個依賴各自開 PR，只有必須同步版本的 Avalonia runtime package family 合併為一張 PR；每張 PR 只在既有 required checks 全部通過後由 GitHub auto-merge；dependency update 不混入非必要 refactor；deprecated report 的修復仍需人工判讀。
- **Do**：只改對應 central version owner；先 focused proof，再跑風險相稱的 gate；CI 是自動合併的唯一版本風險判斷，不另做 major／minor 分流；紅燈只封鎖該依賴 PR，交由人工或 Agent 分析，不自動改產品語義。
- **Proof**：restore、strict build、applicable tests、workflow lint、vulnerable／deprecated package audit。
- **Stop**：若 dependency change 迫使產品語義改變，拆成獨立 scope。
- **Details**：[正式驗證與回滾](operations/verification-and-rollback.md)。

<a id="test-ci"></a>

## Test／CI 卡

- **Use when**：CI、timeout、TRX、zero tests、cleanup、resource contention、CodeQL。
- **Owner**：正式執行由 `DownKyi.CentralTestRunner` 擁有；native behavior 歸對應 OS test project。
- **Invariant**：test project 明列 `DownKyiTestPlatforms`；正式入口只走 runner。PASS 刪 recorder；FAIL 保存 bounded output、cleanup、snapshot；未見 child 不代表不存在。
- **Do**：保存首次失敗；對準 resource／operation，再用同語義 probe。ETW 只對窄目標；CodeQL 保持 `manual` build。
- **Proof**：TRX、recorder、focused regression；無 owner／lifecycle evidence 就寫 `Root cause not proven.`。
- **Stop**：不同 owner／evidence → Pending；不 rerun 洗綠、不加 timing workaround、不改 buildless mode。
- **Details**：[Testing](testing/README.md)；[Targeted Resource Forensics](testing/targeted-resource-forensics.md)。

<a id="download-persistence"></a>

## 下載資料卡

- **Use when**：SQLite、migration、active/history、restore、quarantine、paging。
- **Owner**：active=`DownloadTask` → `DownloadTaskApplicationService` → `IDownloadTaskStore`；history=`DownloadHistoryService` → `IDownloadHistoryStore`；完成 transaction=`IDownloadCompletionStore`；SQL／JSON=`SqliteDownloadTaskStore`。
- **Invariant**：不從 UI／lossy history 重建 active task；短連線、WAL、optimistic version、transaction，不用 process-wide DB lock。Collision 以 active 為準，history quarantine 不得冒充 active evidence。Durable commit 先於 UI；progress 可 bounded／coalesced。
- **Do**：migration 先 backup，單一 transaction DDL，成功後才改 `user_version`。Malformed row 個別 quarantine，不截斷 paging、不記 raw JSON／完整 path／Cookie／URL。啟動載入全部 unfinished + 最新 100 history，其餘 keyset paging。Completion transaction 先於 best-effort cleanup；lossy history 不保 cleanup locator，restart 不補做。
- **Proof**：transition／migration／rollback／collision／paging tests；更新 `SQLite3MC.PCLRaw.bundle` 跑 `LegacySqlCipherCompatibilityTests`。
- **Stop**：第二 persistence owner、UI/history 猜 state、無 migration／rollback 的格式變更。
- **Details**：[Architecture：Download invariant](../ARCHITECTURE.md#download-invariant)。

<a id="transfer-media"></a>

## 傳輸與媒體卡

- **Use when**：queue、content conflict、retry、resume、resource identity、aria2、DURL、mux、FFmpeg、media refresh、HTTP cancellation。
- **Owner**：selection conflict=`DownloadContentConflictResolver`；admission=`DownloadTaskAdmissionService`；finalized media=`DownloadMediaContract`；retry=coordinator；attempt=backend；integrity=validator；FFmpeg lifecycle=`FfmpegProcessRunner`。
- **Invariant**：conflict 在 task 建立前解決並保存 finalized selection；refresh 可換 URL，不可改 transport／quality／codec／audio-video intent。worker 只收 `DownloadTaskId`；backends 共用 key／resume／integrity／persistence；success 仍過 shared integrity；pause／shutdown 保留 GID、partial map、completed keys、progress、version。
- **Do**：built-in resume 先比 resource identity；無 validator 才驗 overlap；mismatch 回 `ResumeRejected`，清該 transfer artifacts 後同地址只重試一次。Delete：persist `Canceled` → stop → 刪 artifacts → 刪 row；failure 不算成功。DURL 只拒絕 duplicate `Order`／無可用地址，不新增 positive／gap／contiguity／start-at-1。Mux 用 same-directory temp、不覆蓋 foreign destination；只有 real decode failure 才撤銷 completed evidence。401／403／schema／cancel 不 retry；429 bounded `Retry-After`。
- **Proof**：content-conflict、`BuiltinRangeDownloaderTests`、`DownloadPipelineStageTests`、delete／integrity tests；loopback 先同步 server 收到 request，不用固定 sleep。
- **Stop**：第二 retry budget／process owner、自訂 DURL 規則、刪除仍有效 source。
- **Details**：[Architecture：Execution 與 retry](../ARCHITECTURE.md#execution-與-retry)。

<a id="settings"></a>

## Settings 卡

- **Use when**：settings、schema、invalid file、flush、shutdown、legacy DES。
- **Owner**：讀=`ISettingsStore.Current` snapshot；寫=typed `Update`；migration=`SettingsSchemaMigrator`。
- **Invariant**：correlated fields 一次 `Update`；operation 中途不重讀。Schema 逐版升級並保留 JSON names；newer schema byte-for-byte 不動；DES 只讀 legacy。
- **Do**：malformed → 唯一 `.invalid-*`；寫入：debounce → async gate → temp UTF-8 JSON → parse → flush → atomic replace；shutdown await `FlushAsync`／`DisposeAsync`。
- **Proof**：`SettingsStoreTests`、`SettingsArchitectureTests`、Host smoke、strict build。
- **Stop**：第二 settings owner、無 migration 改 JSON、operation 中途重讀。
- **Details**：[Architecture：Compatibility invariant](../ARCHITECTURE.md#compatibility-invariant)。

<a id="logging"></a>

## Logging 卡

- **Use when**：`ILogger<T>`、redaction、retention、export、shutdown。
- **Owner**：`ApplicationLogProvider` 是唯一 MEL adapter／redaction boundary；sink／retention／exporter 在 Infrastructure。
- **Invariant**：只注入 `ILogger<T>`；禁止 static `LogManager`、Console、第二 queue／writer。Redaction 早於 NLog／recent buffer；不記 Cookie、token、account、email、完整私人路徑。Queue／buffer bounded，不阻塞 download／UI。
- **Do**：shutdown await `FlushAsync` + `DisposeAsync`，首個 persistence failure 傳回 caller。Export：flush → persisted files → 再 redaction；跳過 malformed 並計數。預設 UTC day、32 MiB rotation、7-day retention、512 MiB cap。
- **Proof**：`ApplicationLogProviderTests` stress、Host smoke、strict build。
- **Stop**：第二 queue／writer、延後 redaction、silent persistence failure。
- **Details**：[Logging Ownership And Sink ADR](design-docs/logging-ownership-sink-adr.md)。

<a id="desktop-host"></a>

## Desktop／Host 卡

- **Use when**：Desktop、DI、Host lifecycle、navigation/dialog、Shell menu、theme、XAML。
- **Owner**：產品組裝=Desktop composition；DI=唯一 Microsoft container；navigation identity=`AppRoute`；route-to-ViewModel=local factory；presentation=`App.axaml` DataTemplates；theme switch=`DesktopThemeController`；tokens=`DesignTokens.axaml`。
- **Invariant**：Domain ← Application ← Infrastructure／Desktop；Infrastructure 不 reference Desktop。`DownKyi` 只組 concrete registrations；禁止 Prism、DryIoc、service locator、global services、第二 root。Shell metadata 保持 local，parent route／payload 保持 caller-owned；route completeness 需跨現有 owners 核對，目前 tests 尚未提供完整的 end-to-end exhaustive gate。不建立 global FeatureRegistry 或第二 router。只有 theme controller 可寫 `RequestedThemeVariant`；presentation 不讀 `Application.Current`／`ResourceDictionary`。`DisableDefaults=true`；新 config provider 不改既有 paths。
- **Do**：新增或調整 routed feature 時核對 route → ViewModel → DI → DataTemplate 與受影響 Shell selection。Long-running work 用 linked scope：caller cancel local，Host stop cancel all。Theme 只用 Fluent + DataGrid；startup 與 settings 都委派 controller；保留 focus、DPI、localization、virtualization。
- **Proof**：typed-route mapping、local composition、Host XAML smoke、`DesktopThemeControllerTests`、Windows packaged startup、CI platform matrix。
- **Stop**：第二 container／router／lifecycle、global FeatureRegistry、反向 reference、global service 繞 composition。
- **Details**：[Architecture：Current owner map](../ARCHITECTURE.md#current-owner-map)；[Desktop Feature Locality ADR](design-docs/desktop-feature-locality.md)。

<a id="bilibili-wbi"></a>

## Bilibili／WBI 卡

- **Use when**：WBI、`-403`、endpoint、envelope、fixture、live audit。
- **Owner**：key validity／single-flight refresh=`IWbiKeyProvider`；sign=`WbiSign(keys,timestamp)`；endpoint／envelope=adapter + fixtures。
- **Invariant**：waiter 不取消 shared refresh；只有 signed `-403` refresh + retry 一次，第二次傳回原錯。Public parsing 不依賴 login timing；partial nav 不清 verified keys。Envelope：video／cheese=`data`，bangumi=`result.video_info`；空 payload 是 failure。
- **Do**：contract 變更同 PR 更新 generated inventory + fixture。Live audit 需授權；authenticated audit 後跑 `script/scan-secrets.ps1`。
- **Proof**：contract fixtures、inventory gate、focused API tests、sanitized audit output。
- **Stop**：第二 key cache、超過一次 retry、static/global client、未授權 live audit。
- **Details**：[Bilibili API Audit](operations/bilibili-api-audit.md)。

<a id="analyzer"></a>

## Analyzer 卡

- **Use when**：strict warning、inventory、suppression、nullable、dispose、fire-and-forget。
- **Owner**：blocking rules=strict build；inventory=`script/analyzer-inventory.ps1`；CSV 是 file／line authority，Markdown 是摘要；CA1501／CA1506 只 advisory。
- **Invariant**：禁止 project-wide `NoWarn`、exclusion、`#nullable disable`、silent severity、新 suppression。改 field／property／collection／name 前查 serialization、SQLite、XAML、reflection、protocol。Resource owner 明確 dispose；fire-and-forget 觀察 fault。
- **Do**：security/correctness → async/lifecycle → performance → public API → style。UI-state await=`ConfigureAwait(true)`；reusable Core/background=`false`。
- **Proof**：clean strict build + [analyzer inventory](../script/analyzer-inventory.ps1)；按變更風險跑 focused tests。
- **Stop**：新 suppression、降 severity、跳過 analyzer 才能綠。
- **Details**：唯一預先核准的 source-local `CA5351` 如下；不得擴到 password、integrity 或其他 trust decision。

| Location | 只允許用途 | Removal gate |
| --- | --- | --- |
| `DownKyi.Core/BiliApi/Sign/WbiSign.cs` | Bilibili WBI protocol MD5 | Bilibili 取代 WBI |
| `DownKyi.Core/Utils/Encryptor/LegacySettingsDecryptor.cs` | read-only legacy migration | 有 telemetry、recovery guidance 的 migration-window 決策 |

<a id="external-binary"></a>

## External Binary 卡

- **Use when**：asset URL、checksum、installer、provenance、TLS、RPC secret、six RID。
- **Owner**：URL／checksum=`external-assets.json`；安裝=installer；official base、source／patch／build provenance 與 trust policy=security docs。
- **Invariant**：immutable URL + checksum；installer 可從 root／`script/` 執行；start 前驗 sidecar／RPC feature。Local aria2 用 loopback + fresh secret + restricted config + `--stop-with-process`，Windows 加 Job Object。Credential 只給 exact HTTPS `bilibili.com` host／subdomain；禁止 downgrade／credential cross-origin redirect。Remote 不由 App 管 lifecycle，non-loopback 要 HTTPS + secure-redirect feature。
- **Do**：固定 commits、機械產生 patch；驗 patch／tree／six-RID digests，再驗 installer／sidecar／TLS／sanitized reports。FFmpeg／ffprobe 驗 target encoder 並保留 CPU fallback。
- **Proof**：manifest、installer、sidecar、six-RID digest／TLS、sanitized reports。
- **Stop**：mutable `latest`、略過 checksum／TLS、擴 credential host、App 管 remote lifecycle。Generic aria2／Motrix 不支援。
- **Details**：[aria2 Security](operations/aria2-security.md)；[FFmpeg Asset Mirroring](operations/ffmpeg-asset-mirroring.md)。

<a id="release"></a>

## Release 卡

- **Use when**：version、tag、exact head、manifest、signing、rollback。
- **Owner**：version／publication=`version.txt` + release workflow；正式命令／rollback=verification doc。
- **Invariant**：version／tag／manifest 一致且 tag immutable；證據只對 exact final commit 有效。Manifest 覆蓋 DownKyi、aria2、FFmpeg、ffprobe、version、SHA-256。macOS sign 後不改 bundle；ad-hoc 不宣稱 Developer ID／notarization／Gatekeeper。
- **Do**：review README／CHANGELOG；跑 canonical procedure；先 push `main` 再 tag；發布後 read back packages／sidecars／manifests。
- **Proof**：exact-head gates、cross-platform packages、`validate-publish-output.ps1`、remote read-back。
- **Stop**：blocker／required gate 未解決：不改 version、不 tag、不 publish；不以單平台 file-exists 取代 content gate。
- **Details**：[Release Policy](refactoring-live-plan.md)；[Verification And Rollback](operations/verification-and-rollback.md)。

## 手動 Smoke 卡

- [ ] 視窗關閉後 process exit；可重新開啟。
- [ ] BV、AV、bangumi、cheese 可解析。
- [ ] 單項、多 P、全選可加入下載；取消 directory picker 不新增 task。
- [ ] Pause → close → reopen 後 resume，不從零開始。
- [ ] 刪除 active task 會移除 media 與 `.aria2`／`.download` sidecars。
- [ ] Subtitle SRT time code 正確。
- [ ] Diagnostic export 不含私人路徑、Cookie、token、敏感 URL。

## 固定名稱

- FFmpeg namespace：`DownKyi.Core.FFmpeg`。
- 禁止 source-directory casing `FFMpeg`。
