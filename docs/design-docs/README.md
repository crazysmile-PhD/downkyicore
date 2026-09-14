# Design Documents

此目錄保存架構決策、邊界審查與設計方案。

- `module-boundary-naming-audit.md`：目前模組邊界與命名一致性審查。
- `list-search-navigation.md`：數字 list URL、投稿／收藏搜尋與返回狀態保留的 typed-navigation 決策。
- `desktop-feature-locality.md`：Desktop routed-feature identity、Shell metadata locality、route completeness 與拒絕全域 FeatureRegistry 的設計決策。
- `logging-ownership-sink-adr.md`：logging privacy boundary、Infrastructure owner、rolling sink、retention 與 diagnostic export 決策。
- `central-test-runner-process-lifecycle.md`：CentralTestRunner 程序生命週期的決定權、階段與驗收邊界（#267 目標契約，尚未驗收）。
- 根層 `ARCHITECTURE.md`：目前與目標拓樸的權威入口。

設計文件描述「為什麼」與責任歸屬；尚未完成的執行步驟只放在 `../refactoring-live-plan.md`。
