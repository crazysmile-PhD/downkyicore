# Execution Plans

Owner 指派工作的短書籤與中斷 checkpoint 位於 GitHub Issue #137；
`../refactoring-live-plan.md` 只保存穩定 release 與 verification policy，不保存
branch、SHA、CI 或目前／下一項工作狀態。此目錄只保留仍有獨立產品或架構
價值的 task plan；完成狀態與 transient evidence 不在 repository 內重複保存。

每個 work item 必須包含：

- 明確目標與 owner branch/PR。
- 影響範圍與不可破壞契約。
- 可執行驗證。
- 完成條件。
- 回滾方式。

設計背景保留在 `../design-docs/`；task plan 保存自己的範圍、驗證與回滾，
不把 branch progress 或歷史完成清單塞回 live plan。
