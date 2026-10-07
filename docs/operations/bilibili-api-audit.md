# Bilibili API Contract Audit

本文件只保存 endpoint inventory 無法推導的政策與例外。固定 endpoint、source path 和 line number 由 current source 生成；wire contract 由 deterministic fixtures／tests 擁有；匿名或登入態 live probe 只產生時點 artifact，不是 current documentation。

## Authority

| Fact | Authoritative owner | Evidence |
| --- | --- | --- |
| 固定 endpoint、source path、line | `DownKyi.Core/BiliApi` | `audit-bilibili-api.ps1 -GenerateSourceInventory` |
| Envelope、required fields、error semantics | DTO／adapter + deterministic fixtures | Core／Infrastructure contract tests |
| WBI key validity／refresh | `IWbiKeyProvider` | WBI tests |
| Optional live status | audit scripts | ignored JSON under `artifacts/bilibili/` |
| Current work／migration decision | [DownKyiCore 工作項目](https://github.com/users/crazysmile-PhD/projects/2) + linked Issue | Project Status + Issue／PR evidence |

Live result、日期、commit SHA、通過數與第三方當時狀態不得抄回 endpoint 表。它們只對執行時的環境和 exact commit 有效。

## Generated inventory

從 repository root 執行：

```powershell
pwsh ./script/audit-bilibili-api.ps1 `
  -GenerateSourceInventory `
  -OutputPath ./artifacts/bilibili/source-inventory.json
```

輸出依 endpoint 排序，包含所有非 `Models` source location；不保存日期或 SHA。`BilibiliApiInventoryArchitectureTests` 會獨立抽取 source endpoints，並要求 generator 的完整結果一致，因此新增 endpoint 不需要再同步 Markdown。

Dynamic subtitle、media、callback 與 web fallback URL 來自 response 或既有 typed input，不屬於 fixed endpoint inventory。這些值保持 opaque，不能寫入診斷日誌。

## Live audit safety

Anonymous probe 仍需明確授權：

```powershell
pwsh ./script/audit-bilibili-api.ps1 `
  -ConfirmLive `
  -OutputPath ./artifacts/bilibili/anonymous-live.json
```

Authenticated probe 只可由明確授權的 operator 執行：

```powershell
pwsh ./script/audit-bilibili-authenticated-api.ps1 `
  -ConfirmAuthenticatedLive `
  -OutputPath ./artifacts/bilibili/authenticated-live.json
pwsh ./script/scan-secrets.ps1
```

- Cookie 只從 `~/.codex/.env` 的 `BILIBILI_TEST_COOKIE` 讀取；不得放入 command line、log、fixture、commit 或 PR。
- `/x/web-interface/nav` 必須同時回 code 0、`data.isLogin=true` 與有效 MID，後續 probe 才可執行。
- Report 只允許 schema 中的 path、status、API code、boolean drift／field checks、outcome 與 safe error type。Query、headers、raw body、account value 與 credential 必須在 process 內丟棄。
- Live artifact 預設不提交。Architecture test 用 `-GenerateContractSample` 在無網路、無 credential 下驗 schema 與 sanitizer boundary，不把一次真實登入成功固定成 regression fixture。

## Non-derived contract exceptions

- `/x/web-interface/nav` 是唯一可在 anonymous response 接受 code `-101` 並讀取 public WBI keys 的 endpoint；其他 nonzero code 保持 typed failure。
- QR generate、poll 與 HTTPS Bilibili callback 必須共用隔離 login session。Parent-domain response cookies 優先於 legacy landing query；只有 atomic persist、reload 並再次通過 `/nav isLogin=true` 才可取代原登入檔。
- Ordinary／cheese envelopes 使用 `data`；bangumi v2 playback 使用 `result.video_info`。Missing／null／empty required payload 都不能被 invent 成 success；preview-only playback 必須由共用 adapter 在進入下載流程前 fail closed，wire markers 由 DTO／adapter 與 deterministic fixtures 擁有。
- Danmaku 先從 `/x/v2/dm/web/view` 取得 `dm_sge.total`，再精確讀取 `1..total`；empty segment 是合法 quiet bucket，不是 EOF。缺少或無效 total 是 protocol failure。
- Favorites search 依 `has_more` 分頁，不能把未篩選的 `media_count` 當 filtered total。History 保留 `/x/web-interface/history/cursor`；watch-later 保留 `/x/v2/history/toview`，除非替代契約有獨立證據。
- Active collection 使用 polymer seasons／series APIs。Legacy channel endpoints 與 ranking／dynamic compatibility surfaces 沒有 current product workflow；不得把 numeric identity 猜測映射到新 contract。
- `/x/relation/stat` 的 active contract 是 numeric user identity；未證實的 nickname lookup 不得轉成 silent fallback。

## Evidence order

1. Deterministic fixture／loopback contract test 證明 parser 與 failure semantics。
2. Generated inventory 證明 current source 真的呼叫哪些 fixed endpoints。
3. Explicit live probe 只辨識執行當下的外部狀態或 drift。
4. yt-dlp、bilibili-api、yutto、bilix 與 community protocol docs 只作 corroboration，不能覆蓋 product tests 或 operator authorization。

Focused gate：

```powershell
pwsh ./script/test-project.ps1 `
  -ProjectPath ./tests/DownKyi.Architecture.Tests/DownKyi.Architecture.Tests.csproj `
  -ClassName DownKyi.Architecture.Tests.BilibiliApiInventoryArchitectureTests
```

若 source、fixture 與 live evidence 不一致，先分類為 source defect、contract drift 或 evidence limitation；不要為了更新表格而改變產品行為。
