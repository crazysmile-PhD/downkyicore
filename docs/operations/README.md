# Operations

- `verification-and-rollback.md`：本機與 CI 驗證、產物、失敗判讀及回滾。
- `aria2-security.md`：aria2 TLS、RPC secret、task header、binary provenance owner、舊設定遷移與六 RID 驗證政策。
- `ffmpeg-asset-mirroring.md`：FFmpeg immutable mirror、updater 權限、失敗恢復與 manifest 驗證。
- `bilibili-api-audit.md`：Bilibili generated endpoint inventory、非推導 contract 例外與 live-audit 安全邊界。
- `../maintenance.md`：依賴、analyzers、external binaries、package 與 release 維護。
- `../performance-baseline.md`：系統效能基準欄位與比較規則。

操作文件必須提供可執行命令與輸出位置，不只描述理論流程。
一般版本歷史由根層 `CHANGELOG.md`、Git tag 與 GitHub Release 擁有，不在
operations 目錄保存重複 release notes。
