# aria2 Transport And Control Security

Status: required runtime contract. DownKyi owns the packaged aria2 child and its loopback JSON-RPC channel；custom aria2 是外部服務，DownKyi 只發 RPC，不負責 start、kill 或 configure。

## Trust and control invariant

- Certificate 與 hostname validation 永遠啟用。Production source、workflow 與 script 禁止 `--check-certificate=false`、insecure TLS callback、`curl -k` 或同類 bypass。
- Packaged RPC 只 bind loopback，禁止 wildcard origin；每次 runtime 產生新 port 與 256-bit secret。只有 supervised child 仍存活時 readiness 才成立，shutdown 也只能處理該 child。
- RPC secret 寫入唯一 temporary config；Unix mode 必須是 `0600`。Startup failure／shutdown 先 bounded wait、必要時 kill-and-wait，child 結束後才刪 config。Secret 與 Cookie 不得出現在 process arguments。
- `AriaClient` 只允許 loopback HTTP；non-loopback 必須 HTTPS，拒絕 user info 與 RPC redirect。
- Transfer header 是 task-local。Cookie 只可送往 exact HTTPS `bilibili.com` host／subdomain。Fork 在實際 follow `Location` 前拒絕 HTTPS downgrade，並在送出 sensitive header 後拒絕 cross-origin redirect；header 中的 CR、LF、NUL 或 control character 也必須拒絕。
- Packaged binary 在 process start 前驗 sidecar SHA-256。Packaged／custom endpoint 都必須從 `aria2.getVersion().enabledFeatures` 回報 `downkyi-secure-redirect-v2`；缺少 integrity 或 capability 時 fail closed。Generic aria2／Motrix 不受支援。
- RPC error 33 固定表示 HTTPS downgrade rejection，34 表示 sensitive-header cross-origin rejection。TLS failure 保持 typed terminal failure，不得轉 empty data、HTTP downgrade 或 automatic retry。

## Legacy `UseSsl` migration

`UseSsl` 不是 current setting。`NetworkSettings` 只保留 private setter-only migration member：舊 JSON 可讀、runtime 仍強制 HTTPS，下一次 atomic settings write 移除該欄位。此 migration 不得改動 SQLite tasks、GID、session、partial map 或 resume state。

## Six-RID executable evidence

`aria2-tls-security` workflow 在 aria2 binary、installer、TLS runtime 或 case owner 變更時，必須以 manifest-pinned real binary 覆蓋六個 RID；也可手動執行。Case owner 是 `Aria2TlsIntegrationTests`，至少保護：trusted download／resume、RPC control、unknown／expired／not-yet-valid CA、hostname／SAN／chain failure、downgrade、credentialed cross-origin redirect、以及 TLS failure 後 partial preservation。它也必須證明失敗不會觸發 downgrade 或第二套 retry。

| Platform | TLS backend | Trust policy |
| --- | --- | --- |
| Windows | WinTLS | elevated runner 用 LocalMachine Root；否則 CurrentUser Root；fixture cert 必須移除 |
| Linux | OpenSSL | temporary system CA；production default discovery，不傳 `--ca-certificate` |
| macOS | AppleTLS | System keychain；bounded install／remove commands |

每個 case 的 report 只保存 environment metadata、backend、aria2 version、case 與 sanitized diagnostic。禁止 header value、request URL、personal path、Cookie、RPC token 或 account identifier。

本機 real-binary gate：

```powershell
$env:DOWNKYI_ARIA2_BINARY = '<absolute aria2c path>'
$env:DOWNKYI_ARIA2_RID = 'win-x64'
$env:DOWNKYI_ARIA2_TLS_REPORT = './artifacts/aria2-tls/win-x64'
pwsh ./script/test-project.ps1 `
  -ProjectPath ./tests/DownKyi.Tests/DownKyi.Tests.csproj `
  -Configuration Release `
  -ClassName DownKyi.Tests.Aria2TlsIntegrationTests `
  -ResultsDirectory ./artifacts/test-results/aria2-local
```

## Provenance ownership

| Fact | Authoritative owner |
| --- | --- |
| Version、build repository／tag／commit、RID URL、archive／binary SHA-256 | `script/assets/external-assets.json` |
| Official base、reviewed source、canonical patch、zlib／OpenSSL source locks | build repository 的 `source-lock.json` |
| Required feature、trust policy、provenance limitations | 本文件 |
| TLS backend mapping | `tests/DownKyi.Tests/Aria2TlsIntegrationTests.cs` |
| System trust install／teardown | `tests/DownKyi.Tests/Aria2TlsTestRuntime.cs` |
| 單次執行的 pass/fail、environment、CI URL | sanitized CI artifact |

Source repository 是 `crazysmile-PhD/downkyi-aria2`；fork line／feature 名稱為 `downkyi-secure-redirect-v2`。Official `release-1.37.0` base commit 是 `02f2d0d8472b3c38c29b4dba8c75ebd5fdd2899a`，reviewed source commit 是 `9938788f7e62af0530a1b28ece752e1de1fd0d46`，canonical patch SHA-256 是 `1234523d1dadedf2342142b656e64b4c67cbca6545afebc584d50f39a229d094`。

Current build identity 由 manifest 固定；source／artifact identity 不等於 reproducible build 或 signed provenance。目前沒有完整 cross-platform reproducible-build 證據、upstream SBOM 或 signed build provenance。

## Incident checks

1. 保存 sanitized TLS report 與 CI URL。
2. 確認 test／app 結束後沒有殘留 `aria2c` child。
3. 執行 `pwsh ./script/scan-secrets.ps1`；可檢查 process argument 結構，不記錄內容。
4. 將 certificate failure 與 DNS、timeout、HTTP status、storage、cancellation 分類。
5. 不要求使用者關閉 TLS；修 trust-store packaging 或 endpoint，再重跑受影響 RID。
