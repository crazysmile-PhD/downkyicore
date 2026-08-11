# Execution Plans

目前即時狀態與 v1.1.1 後續順序位於 `../refactoring-live-plan.md`。
已完成的 v1.1.1 aria2 security Item 1 範圍、驗證與回滾位於
`v1.1.1-security-patch.md`。
目前 PR #120 的範圍收斂契約位於 `v1.1.1-pr-120-scope-cleanup.md`。
下載 runtime P0/P1/P2 清單位於 `v1.1.1-runtime-hardening.md`。
對話框 Issue #123 與啟動模態排序位於 `v1.1.1-dialog-and-startup.md`。
Root-cause review remediation policy baseline 的獨立範圍與驗證位於
`root-cause-review-policy-baseline.md`。
v1.1.1 完成後的 Desktop feature-locality A-to-D migration、baseline、
unknowns、驗收與回滾位於 `desktop-feature-locality.md`。

每個 work item 必須包含：

- 明確目標與 owner branch/PR。
- 影響範圍與不可破壞契約。
- 可執行驗證。
- 完成條件。
- 回滾方式。

完成項目在同一 PR 從即時任務書移除。設計背景保留在 `../design-docs/`，不把歷史完成清單塞回 live plan。
