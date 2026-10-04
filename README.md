# DownKyi Core

<div align="center">

[![GitHub Repo stars](https://img.shields.io/github/stars/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/stargazers)
[![GitHub forks](https://img.shields.io/github/forks/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/network)
[![GitHub issues](https://img.shields.io/github/issues/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/issues)
[![LICENSE](https://img.shields.io/github/license/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/blob/main/LICENSE)

</div>

DownKyi Core 是基于哔哩下载姬 Windows 版与 Avalonia 的跨平台 B 站视频下载工具。项目使用 .NET 10、Avalonia 12、Microsoft Generic Host、Microsoft DI 与 CommunityToolkit MVVM。

## 下载

[![GitHub release](https://img.shields.io/github/v/release/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)
[![GitHub Release Date](https://img.shields.io/github/release-date/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/crazysmile-PhD/downkyicore/total)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)

- Windows：`DownKyi-*-win-x64.zip` 或 `DownKyi-*-win-x86.zip`
- macOS：`DownKyi-*-osx-arm64.dmg` 或 `DownKyi-*-osx-x64.dmg`
- Linux：AppImage / deb / rpm

Windows ZIP 必须完整解压到新目录后再运行。`DownKyi.exe` 旁必须保留 `aria2` 与 `ffmpeg` 子目录；若程序报告缺少 `aria2/aria2c.exe`，请重新下载官方 Release 并完整解压，不要单独补放执行文件。

版本变化见 [CHANGELOG.md](CHANGELOG.md)，安装包见 [GitHub Releases](https://github.com/crazysmile-PhD/downkyicore/releases)。

## 功能

- 解析视频、合集、番剧、课程、收藏、历史记录和稍后再看等入口。
- 下载音频、视频、封面、弹幕、普通字幕和 AI 字幕。
- 支持 aria2 与内置下载器，并保留断点续传需要的状态。
- 删除下载中任务时，同步停止下载器并清理已产生的媒体和临时文件。
- 导出会脱敏 Cookie、token、邮箱、uid 和本机用户路径的诊断日志。

## 运行与数据目录

发布包已包含 .NET、FFmpeg 和 aria2，不需要另外安装运行环境。FFmpeg 优先无损 stream copy；必须转码时会探测可用的硬件 encoder，失败则记录原因并回退到软件编码。

默认数据目录：

- Windows：`%APPDATA%\DownKyi`
- macOS：`~/Library/Application Support/DownKyi`
- Linux：`$XDG_CONFIG_HOME/DownKyi`，未设置时通常为 `~/.config/DownKyi`

常用子目录：

- `Media`：默认下载目录
- `Logs`：应用和诊断日志
- `Storage`：SQLite 下载数据库
- `Config`：设置与登录信息
- `Cache`：图片和运行缓存
- `Aria`：aria2 session 与日志

设置 `DOWNKYI_DATA_DIR` 可指定数据根目录。设置 `DOWNKYI_PORTABLE=1`，或在程序目录放置 `portable`、`.portable`、`DownKyi.portable`，可启用便携模式。

## 登录

无法扫码时，可在登录页面粘贴已登录 Bilibili 请求中的 `Cookie` header。程序会沿用二维码登录的保存和验证流程；验证失败、网络异常或取消时恢复原有凭据。Cookie 会保持已经编码的传输形式，正式 `Login` 文件由程序管理，无需手动编辑。

## 诊断

关于页面可打开日志目录或导出诊断日志，用于排查网络请求、下载器启停和续传、字幕／弹幕／封面处理、音视频合并以及退出清理。导出过程会过滤普通调试噪音并遮蔽敏感信息。

## 开发者入口

开发需要 .NET 10 SDK。按问题类型进入唯一 owner：

- [AGENTS.md](AGENTS.md)：修改协议、导航顺序和禁止事项。
- [ARCHITECTURE.md](ARCHITECTURE.md)：当前拓扑、边界、invariant 与可执行防线。
- [docs/maintenance.md](docs/maintenance.md)：按领域组织的维护卡。
- [docs/testing/README.md](docs/testing/README.md)：测试基础设施和失败分类。
- [docs/operations/verification-and-rollback.md](docs/operations/verification-and-rollback.md)：正式验证与回滚命令。
- [DownKyiCore 工作項目](https://github.com/users/crazysmile-PhD/projects/2)：唯一工作管理入口，负责 Priority、Status、排序、Draft／待验证／待决定、In Progress 与 Done；单项工作的背景、范围、证据、验收与 PR 关联保存在对应 Issue。

本机运行：

```powershell
dotnet run --project .\DownKyi\DownKyi.csproj
```

## 免责声明

1. 本软件只提供视频解析，不提供资源上传或服务器存储功能。
2. 本软件仅解析来自 B 站的内容；格式修复、分段拼接或硬件加速流程可能进行转码和索引重建。
3. 解析内容的版权归原作者所有，内容提供者与上传者应承担相应责任。
4. 所有内容仅供学习交流；未经授权不得用于其他用途，请支持原始发布者与原创内容。
5. 因使用本软件产生的版权问题，软件作者概不负责。

许可与第三方归属见 [LICENSE](LICENSE) 和 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
