# Testing

測試分層：

- `DownKyi.Domain.Tests`：state transitions 與 value objects。
- `DownKyi.Application.Tests`：commands、queries、coordinators 與 ports。
- `DownKyi.Infrastructure.Tests`：SQLite、migration、write-behind 與 adapters。
- `DownKyi.Core.Tests`：Bilibili contracts、HTTP、settings、logging、FFmpeg、aria2。
- `DownKyi.Desktop.Tests`：real Host、XAML 與 typed navigation smoke tests。
- `DownKyi.Tests`：目前 executable compatibility 與 end-to-end service tests。
- `DownKyi.Architecture.Tests`：依賴方向、禁止模式、AI environment 與 debt ratchets。

重要文件：

- `module-boundary-ratchets.md`
- `../maintenance.md`
- `../operations/verification-and-rollback.md`

測試不得讀取使用者真實 settings、cookie、下載 DB 或 aria2 session。網路 contract tests 使用 fixture 或 loopback server。
