# Historical Source Archive

本文件只索引 DownKyiCore 1.0.24 及更早版本的原始碼。版本變更內容仍由
根目錄 `CHANGELOG.md` 管理；本文件不重複 release notes，也不改變 `main` 的
提交歷史或發布流程。

## 封存邊界與來源

- `archive/upstream-history` 指向真正的上游 1.0.24 release-preparation commit
  `0ee21336bab983668396c61d435a376f5eb07f71`。從最早的
  `face3bfa69c288e24c93fa1697b721669d12dfb9` 到該 commit 共 315 個 commit、
  26 個 merge commit，原作者、作者時間、父鏈與 commit SHA 均未改寫。
- 主要來源是原作者倉庫 `https://github.com/yaobiao131/downkyicore` 的既存
  Git objects。該倉庫目前只公開 `deprecated` branch，但原始鏈仍由 GitHub
  pull refs 保存；本倉庫既有 pull refs 也已保存相同 objects。
- `https://github.com/hydrogen-shelter/downkyicore-API` 用於交叉核對。它在
  `0ffd06605976913213443f20f220ee8e37a64339` 分岔，只有 1 個自身 README
  commit，並缺少上游通往 1.0.24 的最後 5 個 commit，因此不是 1.0.24 的
  權威來源。
- `archive/v1.0.0` 到 `archive/v1.0.24` 是 2026-10-09 建立的**封存標籤**，
  不是仍存續的原始上游標籤，也不是 GitHub Release。`archive/` 前綴不符合
  正式發布 workflow 的 `v*` tag trigger。
- `main` 與 `archive/upstream-history` 仍是互不相連的歷史；沒有執行
  `--allow-unrelated-histories` merge、B1 ancestry-link merge、force push 或
  history rewrite。

## 版本對照

每一列都由唯一的 release／prepare commit subject 與該 commit 的
`version.txt` 交叉確認；標籤 peel 後仍是原始 commit SHA。

| 版本 | 原始 commit | 作者日期（UTC+08:00） |
| --- | --- | --- |
| 1.0.0 | `635f9e1b7b29cdc9fe1c9884da3d9172366f21c7` | 2023-12-19 23:48:21 |
| 1.0.1 | `c325649b411cb38510ccfd5a2d2d396cfdcfe671` | 2023-12-20 23:57:46 |
| 1.0.2 | `9589ab72f144543cb6ec79a86abf2a083722ed49` | 2023-12-21 23:26:46 |
| 1.0.3 | `998078923ad9354f3acd6d5798fae0756a947a7d` | 2023-12-24 22:31:48 |
| 1.0.4 | `8da8aed16f9e5c32057d693e8b24d017de9a11e4` | 2023-12-27 22:16:52 |
| 1.0.5 | `fd890cb19e92176db1ea6284cfd230e1f8c7a2a4` | 2024-01-01 20:22:18 |
| 1.0.6 | `4d7c4a6d268fc3ff3d907f82ee9bb8e7469d6ec3` | 2024-01-15 20:46:28 |
| 1.0.7 | `8490ce88b8e9aacec7c2ca5a85c4b4af1f377987` | 2024-02-04 22:28:41 |
| 1.0.8 | `524ab8ee92de0190d43867b9b628465f9a725059` | 2024-03-05 21:31:26 |
| 1.0.9 | `b91a7c9ec6260c2f4fdda33d3673ccb48bed87d6` | 2024-04-08 22:00:08 |
| 1.0.10 | `1a9ec06ec2c3b7db51a8b401be9b715cb8d442bb` | 2024-05-07 22:28:25 |
| 1.0.11 | `75406b5b55816e5f81be1db244aae4b559984952` | 2024-08-05 23:01:24 |
| 1.0.12 | `d203e57414559b3f858042430eef14bd246bff22` | 2024-10-05 20:14:06 |
| 1.0.13 | `45f2a5098a7ddb24a7e958ef63a5bb78d76296fd` | 2024-10-14 23:20:55 |
| 1.0.14 | `bbfdddb5b7a60c6815aed1378217af1e43219f31` | 2024-11-25 15:37:03 |
| 1.0.15 | `f70694c0894db920d40c3c74b7f00f1a74ee3453` | 2024-11-26 21:22:16 |
| 1.0.16 | `07e01b5c0da06a1adb98b2986c1889d6661cd3db` | 2024-11-29 10:01:02 |
| 1.0.17 | `d23b7a2b7556b17b846a95e01e509a773473bc08` | 2025-03-19 21:56:47 |
| 1.0.18 | `d47a8809139c0c2ec7b8461934fc345c083d0761` | 2025-04-25 20:22:54 |
| 1.0.19 | `5810a6eadf00425812f64c5410e57758e096abeb` | 2025-04-30 10:18:00 |
| 1.0.20 | `a3dcb95901c1614ab3495371739458df69c2437f` | 2025-05-08 23:33:02 |
| 1.0.21 | `d9b1558e75140dc4080045016179506ba90134fc` | 2025-08-10 17:11:09 |
| 1.0.22 | `b5299f5db71a51311ec82570ec3d37c02854d972` | 2025-08-19 23:07:39 |
| 1.0.23 | `9c1336b986b6f7fcead6782ef9e0b52e6e1a62d4` | 2025-09-23 22:53:54 |
| 1.0.24 | `0ee21336bab983668396c61d435a376f5eb07f71` | 2026-04-01 22:30:50 |

原始倉庫目前沒有可取得的舊 tag objects 或 GitHub Releases。因此上表能確認
版本對應的原始 commit，但不能聲稱封存標籤本身是原作者當時建立的 tag。

## 1.0.24 快照核對

提供的 `downkyicore-main.zip`：

- SHA-256：`CC289F9EA0E93230B8398F443D2E162EFDD18ED4E5E9C32F9300BA9B04CB82C5`
- Git tree：`4af78c27a9538b549b8143fcc76ef5c46002a4a7`
- 與本倉庫既有 commit `9e8b9af1ce9e583884b208e4b5214ae6c32cdee5`
  的 tree 完全相同。

該 commit 是接手後建立的原始碼快照，不是原作者的 1.0.24 release commit。
它與真正的 `0ee21336bab983668396c61d435a376f5eb07f71` 相差 9 個檔案、146 行新增、
57 行刪除。因為已找到真正的上游 commit，且快照早已是 `main` 的祖先，所以
沒有再建立重複的 snapshot branch。

## 匯入前安全檢查

- Gitleaks 8.30.1 掃描 `0ee2133` 的完整祖先鏈。7 筆 generic-key 候選經人工
  分類為 4 個 Avalonia 色彩資源與 3 個 GUID 形式的本機 SQLite／SQLCipher
  相容常數；沒有 Cookie、使用者資料檔、私鑰或可授權遠端服務的憑證。
- 這些 Git objects 在本倉庫既有 pull refs 與原作者公開倉庫中已可讀取；封存
  refs 沒有新增先前未公開的秘密內容。
- 歷史中最大的 4 個 blobs 是當時隨附、後來移除的 aria2 執行檔，大小為
  5.86–7.27 MiB。沒有 100 MiB 以上的 object，也沒有資料庫、log、`.env`、
  certificate 或 private-key 檔案。

## 查閱與比較

```powershell
# 取得封存 branch 與 tags
git fetch origin archive/upstream-history
git fetch origin 'refs/tags/archive/*:refs/tags/archive/*'

# 查看原作者、作者時間、committer 與父鏈
git show --format=fuller --stat 'archive/v1.0.24^{commit}'
git log --graph --decorate --oneline origin/archive/upstream-history

# 查看某版本的檔案或完整 tree
git show 'archive/v1.0.20^{commit}:version.txt'
git ls-tree -r --name-only 'archive/v1.0.20^{commit}'

# 比較兩個舊版本
git diff --stat 'archive/v1.0.20^{commit}' 'archive/v1.0.24^{commit}'
git diff 'archive/v1.0.20^{commit}' 'archive/v1.0.24^{commit}' -- DownKyi.Core

# 比較真正上游 1.0.24 與接手後快照
git diff --stat 'archive/v1.0.24^{commit}' 9e8b9af1ce9e583884b208e4b5214ae6c32cdee5
```

## 歷史版本重建

獨立的手動轉換器位於 `script/historical-rebuild/`，操作與安全邊界見該目錄的
`README.md`。它只從本表既有的 25 個 `archive/v*` 標籤建立臨時 worktree，
不移動標籤、不修改舊 commit，也不使用正式 `v*` 發布 pipeline。成功產物會
上傳到各封存標籤對應的 Draft Release，並附 checksum、來源 commit、相容性
調整、簽章限制與啟動驗證紀錄。
