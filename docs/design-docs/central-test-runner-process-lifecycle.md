# CentralTestRunner 程序生命週期責任圖

這裡列的是決定權與修改入口，不複製每個方法的內部結構。
跨平台實作狀態以 PR #267 的 exact-head CI 與 review 為準。

修改程序啟動、取消、清理或失敗回報時，先讀此頁。檔案行數不能決定誰是 owner；
要看誰有權判斷「哪些程序屬於本次執行」及「本次生命週期是否完成」。

## 責任與決定權

| 要回答的問題 | 唯一決定者 | 其他參與者 |
| --- | --- | --- |
| 哪些程序屬於本次執行？ | `ProcessLifecycleOwner` 依 workload 啟動前建立的 OS containment 判定 | Windows Job、Linux cgroup／選定的 process group、macOS process group 實作或觀察 containment；PID 清單與 snapshot 不能授權終止 |
| 何時開始及完成清理？ | `ProcessLifecycleOwner` 持有階段轉換、同一個 monotonic `CleanupDeadline`、終止、停止確認、owned task join 和 helper reap | `FlightRecorderExecution`、`BuildProcessRunner` 只能請求執行或取消，不能各自宣告清理成功 |
| 哪個失敗是主因？ | `ProcessLifecycleOwner` 保留已確定的 primary failure 與 live-state evidence；後續清理錯誤列為 secondary | `FlightRecorder` 只負責遮蔽敏感資料與保存證據，不選 root cause |
| 命令應回報什麼？ | Owner 提供一個 terminal lifecycle outcome，包含清理是否已證實完成 | 呼叫端把 typed outcome 映射到公開 exit／cancellation 契約；不能從 snapshot 或各自的 `stopped`／`drained` 布林值推論成功 |
| OS 當時呈現什麼狀態？ | 平台 inspector 與 `ProcessTreeSnapshot` 提供觀察 | 觀察結果是 evidence，不是第二套 ownership 系統 |

平台 backend 可以執行 Job、cgroup 或 process-group 操作並回報結果；它不能
另選 owner、延長 deadline，或自行宣告本次執行成功。

## 生命週期契約

```text
請求
  -> containment 已就緒
  -> 允許 workload 啟動
  -> running
  -> root exit / cancellation / timeout / fault
  -> owner 控制的單次清理
  -> containment 已停止 + owned work 已結束 + helper 已回收
  -> terminal outcome
```

- Workload 開始前就要建立 containment；取消時不能用 PID tree、PID 出生時間或
  snapshot 重建 ownership。
- 回傳 cancellation success（130）前，原 owned descendants 必須已停止執行。
  gone、zombie (`Z`)、dead (`X`) 可接受；running／sleeping 不可接受。
  Owner 可在既有 deadline 內確認 live→stopped 收斂。若成功回傳後第一次觀察
  仍為 live，就是失敗；測試不能再做第二次 `ps` 或給 grace period。
- Owner 回傳前，所有 owner-created task 必須 terminal，所有 helper process
  必須 exited／reaped。外層 `WaitAsync` 超時不代表 underlying work 已停止。
- Unix launch host 在 root exit 後仍保持存活，直到 owner 終止並確認 process
  group 停止後才 reap；存活的 session leader 保留 group identity。host 不持有
  workload 的輸出 pipe：它把 root 的 stdout／stderr 經兩條獨立通道交給 owner，
  每條轉送工作在來源 EOF 時關閉通道，並在兩條工作結束後回報結果。owner 須
  join 讀取工作並確認轉送結果，才可宣告正常完成。控制通道由 owner 持續讀到
  host EOF；取消與轉送失敗同時發生時，清理後仍須 join 該讀取工作並保留失敗。
- Owner 的 pipe reader 只收集經遮蔽且有容量上限的輸出尾段，不等待任意
  `TextWriter`。成功時不顯示尾段並清除 recorder；失敗／取消／逾時時以 recorder 留存尾段。
  完整即時轉送若再成為需求，須另設可終止的輸出邊界，不能放回 owner task。
- 全部清理階段共用同一個 monotonic `CleanupDeadline`；期限耗盡不能回 130。
  若工作無法在期限內停止並 join，就需要可終止的執行邊界，不能放棄工作或
  暗中增加 budget。
- 若最後一次有效觀察仍為 live，而 inspector 在證實停止前失敗，owner 必須
  保留該 live evidence 並以「未證實停止」為主因；inspector／reap exception
  是次因。期限到達或成功回傳後觀察到 live 也不能被稍後結果洗掉。
  Recorder 只保存 owner 的因果順序。
- Root 逾時或非零退出在清理前固定為主因；`CleanupSucceeded` 只表示清理完成，
  不代表測試通過。後續清理或 evidence 失敗使最終 exit code 為 2，但只列為次因。

## 修改時從哪裡讀起

| 修改內容 | 先讀 | 再讀必要的相鄰邊界 |
| --- | --- | --- |
| 啟動、containment、清理、output task lifetime | [ProcessLifecycleOwner.cs](../../tools/DownKyi.CentralTestRunner/ProcessLifecycleOwner.cs) | 被選用的 OS backend／inspector 及其[平台測試](../../tests/PlatformShared/ProcessLifecycleOwnerPlatformTests.cs) |
| Build 或測試命令行為 | [BuildProcessRunner.cs](../../tools/DownKyi.CentralTestRunner/BuildProcessRunner.cs) 或 [FlightRecorderExecution.cs](../../tools/DownKyi.CentralTestRunner/FlightRecorderExecution.cs) | Owner 的 terminal outcome；這兩處只能請求並呈現結果 |
| 程序狀態診斷 | [FlightRecorder.cs](../../tools/DownKyi.CentralTestRunner/FlightRecorder.cs)、[ProcessTreeSnapshot.cs](../../tools/DownKyi.CentralTestRunner/ProcessTreeSnapshot.cs) | 平台 observer；snapshot 不能導向 destructive ownership |
| 130／2、失敗優先序、期限 | Owner 的 terminal outcome、[CleanupDeadline.cs](../../tools/DownKyi.CentralTestRunner/CleanupDeadline.cs) | 取消與失敗路徑回歸，再看命令層映射 |

遷移驗收時，同時檢查 build 與測試啟動路徑：呼叫端不得保留獨立的 stop／drain
決定，`Dispose` 不得形成較短的清理路徑，diagnostic capture 不得延誤必要清理。
這些是每次修改都要維持的審查條件。

已知界限：Windows 失敗 recorder 的 Toolhelp 程序關係快照仍是同步呼叫；
它在 owner 清理完成後執行，但無法由 `SnapshotWindow` 強制中斷，故失敗命令的
診斷階段目前沒有硬返回期限。此觀察不參與 ownership，後續處理見 issue #269。
