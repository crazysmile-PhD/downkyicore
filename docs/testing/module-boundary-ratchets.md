# Module Boundary Ratchets

`ModuleBoundaryBaselineTests` 不是 suppression。它把 2026-07-22 已確認的邊界與命名債務設為最大集合：

- 現有 violation 被刪除：pass。
- 現有巨檔縮小：pass。
- 新增 violation：fail。
- 已知巨檔增長：fail。

## 為何不用「現況先紅」

把必然失敗的 architecture tests 合併到 PR 會讓 CI 永久失去訊號。後續任何真正 regression 都會被既有紅燈掩蓋。因此本專案使用 ratchet，讓主線保持綠色並只允許債務下降。

## 更新規則

1. 先執行 `script/audit-module-boundaries.ps1`。
2. 若新增 violation，修正程式碼，不可直接加入 baseline。
3. 若是外部 protocol 或 generated code 的必要例外，先新增 ADR、來源與 test，再以最小項目更新 baseline。
4. 若移除 debt，刪除已不需要的 baseline entry。
5. 同步更新 `module-boundary-naming-audit.md`、live plan 與 knowledge graph。

全域「simple type name 不可重複」與「檔名必須等於第一個宣告型別」規則不採用，因為 protocol DTO、partial class、`.axaml.cs` 和 companion records 會被誤判。
