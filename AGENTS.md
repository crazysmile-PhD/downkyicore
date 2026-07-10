# AGENTS.md — DownKyi 代码库 AI 编码代理指南

本文件为 AI 编码代理在此代码库中高效工作提供所需的全部信息。

## 强制架构入口

> 在分析或修改 DownKyi 程式碼前，必須先閱讀 `ai-knowledge-graph.md`，確認受影響的節點、依賴關係、穩定契約、風險與對應測試。

本仓库中的实际路径是 `docs/ai-knowledge-graph.md`。任何新增、删除、移动或重新导向模块责任与依赖关系的变更，都必须在同一个分支、commit 组和 Pull Request 中同步更新该文件。执行重构时还必须阅读 `docs/refactoring-live-plan.md`，遵守其中的分支分组与未完成工作清单。

---

## 目录

1. [项目概述](#项目概述)
2. [仓库结构](#仓库结构)
3. [构建与运行](#构建与运行)
4. [测试](#测试)
5. [架构](#架构)
6. [命名规范](#命名规范)
7. [代码风格](#代码风格)
8. [MVVM 模式](#mvvm-模式)
9. [异步模式](#异步模式)
10. [错误处理](#错误处理)
11. [核心工具类](#核心工具类)
12. [包管理](#包管理)

---

## 项目概述

**DownKyi** 是一个跨平台的 Bilibili 视频下载器，使用以下技术构建：

- **Avalonia 12** — 跨平台 XAML UI（Windows、Linux、macOS）
- **Prism.DryIoc.Avalonia** — MVVM 框架、依赖注入、导航、发布/订阅事件
- **Microsoft.Data.Sqlite** — SQLite 原生 SQL 存储（下载队列、历史记录）
- **FFMpegCore** — 音视频混流
- **Downloader** — 内置 HTTP 下载引擎
- **Newtonsoft.Json** — 设置项与 API 响应的序列化

当前旧生产代码包含两个项目，此外还有四个测试项目与一个 benchmark 项目。PR 02 会新增目标分层项目：

| 项目 | 类型 | 目标框架 |
|---|---|---|
| `DownKyi` | WinExe（Avalonia UI） | `net10.0` |
| `DownKyi.Core` | 类库 | `net10.0` |
| `tests/*` | xUnit v3 测试 | `net10.0` |
| `benchmarks/DownKyi.Benchmarks` | BenchmarkDotNet | `net10.0` |

目标运行时：`win-x64`、`win-x86`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。

---

## 仓库结构

```
DownKyi/
├── AGENTS.md                       ← 本文件
├── DownKyi.sln
├── Directory.Build.props           ← 全局配置：Nullable=enable，CPM=true
├── Directory.Build.targets         ← 空文件
├── Directory.Packages.props        ← 所有 NuGet 包版本（CPM）
├── version.txt                     ← 版本号的唯一来源
├── .github/workflows/build.yml     ← CI：全平台构建与发布
│
├── DownKyi/                        ← UI 项目
│   ├── App.axaml.cs                ← Prism DryIoc 引导、DI 注册
│   ├── Program.cs                  ← Avalonia AppBuilder 入口点
│   ├── AppConstant.cs              ← 全局应用常量
│   ├── ViewModels/                 ← MVVM ViewModel（Prism BindableBase）
│   │   ├── ViewModelBase.cs        ← 基类，含属性变更辅助方法
│   │   ├── Dialogs/                ← 对话框 VM（IDialogAware）
│   │   └── DownloadManager/        ← 下载列表项 VM
│   ├── Views/                      ← Avalonia .axaml 视图（CompiledBindings）
│   ├── Services/                   ← IInfoService、IDownloadService 抽象
│   │   └── Download/               ← DownloadService 及内置/Aria 实现
│   ├── Events/                     ← Prism PubSubEvent<T> 定义
│   ├── Models/                     ← 数据库实体（纯 POCO，无 ORM 注解）
│   ├── PrismExtension/             ← 自定义异步 IDialogService 扩展
│   └── Utils/                      ← DictionaryResource（国际化）
│
└── DownKyi.Core/                   ← 领域/API 项目
    ├── BiliApi/                    ← Bilibili HTTP API
    │   ├── WebClient.cs            ← 单例 HttpClient 封装
    │   ├── VideoStream/            ← playURL / 字幕 API
    │   └── BiliUtils/              ← URL 解析、BvId <-> AvId 转换
    ├── Settings/                   ← SettingsManager 单例（partial 类）
    ├── Storage/                    ← StorageManager（路径解析）、SQLite DB
    ├── Logging/                    ← LogManager（异步文件写入器）
    ├── FFMpeg/                     ← FFMpegCore 封装
    ├── Aria2cNet/                  ← aria2 RPC 客户端
    └── Utils/                      ← Format、HardDisk、Debugging.Console、Encryptor
```

---

## 构建与运行

### 前置条件

- .NET 10 SDK
- 系统 PATH 中存在 FFmpeg 二进制文件（开发时混流操作所需）
- `PupNet` 工具（仅打包时需要，开发构建无需）

### 常用命令

```bash
# 还原包
dotnet restore

# Debug 构建
dotnet build

# Release 构建
dotnet build -c Release

# 运行应用
dotnet run --project DownKyi/DownKyi.csproj

# 自包含发布（示例：macOS arm64）
dotnet publish DownKyi/DownKyi.csproj \
  --self-contained \
  -r osx-arm64 \
  -c Release \
  -p:DebugType=None \
  -p:DebugSymbols=false
```

`-r` 支持的值：`win-x64`、`win-x86`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。

---

## 测试

本仓库使用 xUnit v3，并包含以下测试层：

- `tests/DownKyi.Core.Tests`：HTTP、aria2、FFmpeg 与 Core 行为。
- `tests/DownKyi.Tests`：下载流程、储存相容性与应用服务。
- `tests/DownKyi.Desktop.Tests`：Avalonia headless UI smoke tests。
- `tests/DownKyi.Architecture.Tests`：项目依赖方向与循环引用。

提交前至少依序执行：

```bash
dotnet restore ./DownKyi.sln
dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental
dotnet test ./DownKyi.sln -c Release --no-restore --no-build
dotnet format ./DownKyi.sln --verify-no-changes --no-restore
git diff --check
```

不要并行执行同一工作树的 `dotnet build` 与 `dotnet test`，两者可能同时写入 PDB 而产生假失败。测试必须通过 `DOWNKYI_DATA_DIR` 使用隔离目录，禁止读取真实 Cookie、设置或下载数据库。

---

## 架构

`docs/ai-knowledge-graph.md` 是当前代码责任、调用关系、稳定契约、风险和测试入口的权威索引。下述 Prism 架构属于迁移中的旧实现；新代码必须遵守 `DownKyi.Desktop -> DownKyi.Application -> DownKyi.Domain`，以及 `DownKyi.Infrastructure -> Application/Domain` 的目标依赖方向。

### 依赖关系

```
DownKyi（UI） → DownKyi.Core（领域/API）
```

`DownKyi.Core` 不依赖 `DownKyi`。所有 Bilibili API 调用、设置、日志和存储均在 `Core` 中实现。

### Prism DI 注册

所有服务在 `App.axaml.cs` 的 `RegisterTypes` 方法中注册，接口通过 DryIoc 容器解析。ViewModel 中使用构造函数注入，尽量少用 `IContainerProvider`。

### 导航

使用 Prism 区域导航。视图通过区域名称注册自身。导航通过 `IRegionManager.RequestNavigate(regionName, viewName, parameters)` 触发。ViewModel 实现 `INavigationAware`（`OnNavigatedTo`、`OnNavigatedFrom`、`IsNavigationTarget`）。

### 事件（发布/订阅）

事件定义在 `DownKyi/Events/`。在 ViewModel 中注入 `IEventAggregator` 使用。示例：

```csharp
_eventAggregator.GetEvent<NavigationEvent>().Publish(param);
_eventAggregator.GetEvent<MessageEvent>().Publish(message);
```

---

## 命名规范

| 目标 | 规范 | 示例 |
|---|---|---|
| 私有实例字段 | `_camelCase` | `_inputText`、`_downloadService` |
| 公共属性 | `PascalCase` | `InputText`、`VideoSections` |
| `static readonly` 字段 | `PascalCase` | `LogQueue`、`HttpClient` |
| `const` 字段 | `PascalCase` | `Tag`、`NullMark`、`Retry` |
| 方法 | `PascalCase` | `ExecuteInputCommand`、`GetVideoPages` |
| 命令后备字段 | `_xyzCommand` | `_inputCommand` |
| 命令属性 | `XyzCommand` | `InputCommand` |
| 命令执行方法 | `ExecuteXyzCommand` | `ExecuteInputCommand` |
| 命令可执行断言 | `CanExecuteXyzCommand` | `CanExecuteInputCommand` |
| 类级别标签常量 | `public const string Tag` | `"PageVideoDetail"` |
| 接口 | `IPascalCase` | `IDownloadService`、`IInfoService` |

---

## 代码风格

### 文件范围命名空间（C# 10）

所有文件使用文件范围命名空间：

```csharp
namespace DownKyi.ViewModels;

public class MyViewModel : ViewModelBase { }
```

### using 指令

顺序：`System.*` → 第三方 → 项目内部。存在名称冲突时使用别名：

```csharp
using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Prism.Commands;
using DownKyi.Core.Logging;
using Console = DownKyi.Core.Utils.Debugging.Console;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;
```

### 隐式 using

- `DownKyi.Core`：`<ImplicitUsings>enable</ImplicitUsings>` — 无需手动添加 `using System;` 等。
- `DownKyi`（UI）：隐式 using **已禁用** — 所有 using 必须显式声明。

### 可空引用类型

通过 `Directory.Build.props` 全局启用。所有可能为 null 的引用类型字段和参数均须加 `?` 注解。仅在无法避免时用 `!`（null 宽恕运算符）抑制警告。

### Region 块

使用带中文标签的 `#region` / `#endregion` 组织 ViewModel 文件的逻辑结构：

```csharp
#region 页面属性申明   // 页面属性声明

#region 命令申明       // 命令声明

#region 业务逻辑       // 业务逻辑
```

### 注释

代码库中中英文注释并存，新代码使用任意语言均可。

---

## MVVM 模式

### ViewModel 基类

所有 ViewModel 继承 `ViewModelBase`（继承自 `Prism.Mvvm.BindableBase`）。对话框 ViewModel 还需继承 `BaseDialogViewModel`（实现 `IDialogAware`）。

### 可绑定属性

```csharp
private string? _inputText;
public string? InputText
{
    get => _inputText;
    set => SetProperty(ref _inputText, value);
}
```

### 命令（使用空合并运算符懒初始化）

```csharp
private DelegateCommand? _backSpaceCommand;
public DelegateCommand BackSpaceCommand =>
    _backSpaceCommand ??= new DelegateCommand(ExecuteBackSpace);

private void ExecuteBackSpace() { /* ... */ }
```

带可执行条件的命令：

```csharp
private DelegateCommand? _confirmCommand;
public DelegateCommand ConfirmCommand =>
    _confirmCommand ??= new DelegateCommand(ExecuteConfirm, CanExecuteConfirm)
        .ObservesProperty(() => IsReady);

private bool CanExecuteConfirm() => IsReady;
private void ExecuteConfirm() { /* ... */ }
```

### 每个 ViewModel 的标签常量

每个 ViewModel 须声明用于日志的标签：

```csharp
public const string Tag = "PageVideoDetail";
```

### UI 线程调度

所有从非 UI 线程发起的属性变更，均须调度回 UI 线程：

```csharp
// 异步方式（async 方法中首选）
await Dispatcher.UIThread.InvokeAsync(() => MyProperty = value);

// 同步方式（仅在同步上下文中使用）
Dispatcher.UIThread.Invoke(() => MyProperty = value);

// ViewModelBase 上的辅助方法
App.PropertyChangeAsync(() => MyProperty = value);
```

### ObservableCollection

对于可能被并发修改的集合，使用 `ImmutableObservableCollection<T>`（定义于 `DownKyi/ViewModels/ImmutableObservableCollection.cs`）代替 `ObservableCollection<T>`，以避免枚举过程中被修改的异常。

---

## 异步模式

### 命令处理器

命令处理流程必须返回 `Task`，并通过现有异步命令或后续 CommunityToolkit AsyncRelayCommand 接线。除 UI 事件处理器外禁止新增 `async void`：

```csharp
private DownKyiAsyncDelegateCommand? _loadCommand;
public DownKyiAsyncDelegateCommand LoadCommand =>
    _loadCommand ??= new DownKyiAsyncDelegateCommand(ExecuteLoadAsync);

private async Task ExecuteLoadAsync()
{
    try
    {
        await _useCase.ExecuteAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception e)
    {
        LogManager.Error(Tag, e);
    }
}
```

### 后台工作

ViewModel 不得使用 `Task.Run` 包装同步网络、数据库或业务流程。阻塞 API 必须在 Infrastructure 边界明确隔离；ViewModel 只 await 可取消的 Application use case：

```csharp
var result = await _useCase.ExecuteAsync(cancellationToken);
await Dispatcher.UIThread.InvokeAsync(() => MyCollection.Add(result));
```

### 取消操作

长时间运行的循环接受 `CancellationToken`。使用 `SemaphoreSlim` 限制并发数量：

```csharp
var semaphore = new SemaphoreSlim(maxConcurrent);
await semaphore.WaitAsync(cancellationToken);
try { /* 工作 */ }
finally { semaphore.Release(); }
```

---

## 错误处理

### 标准模式

所有异常边界必须保留 stack trace，并通过统一 diagnostics 记录。禁止为了日志重复输出敏感路径、Cookie、Token 或完整 URL；禁止依赖 Console wrapper 作为错误契约。用户可见失败应转换为 typed UI state 或通知：

```csharp
catch (Exception e)
{
    LogManager.Error(Tag, e);
    return OperationResult.Failure(OperationError.Unexpected(e));
}
```

### 失败契约

新 Application 与 Infrastructure API 必须使用 typed result 或明确例外，不得把网络、JSON、数据库或外部程序错误伪装成 `null`、空字串或空集合。`OperationCanceledException` 必须保留取消语意。

### HTTP 重试

旧 `WebClient` 使用迭代式、可取消的有限重试；重试耗尽或 HTTP 200 空回应会抛出 `HttpRequestException`。后续 typed Bilibili client 必须区分可恢复错误、429 `Retry-After`、不可重试的 401/403/schema 错误与取消。

### 有意吞掉异常

禁止空 catch。仅在关闭、清理或释放等明确 best-effort 边界捕获可预期例外，并至少写入经过遮蔽的 diagnostics；取消必须重新抛出：

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    throw;
}
catch (IOException e)
{
    LogManager.Error(Tag, e);
}
```

---

## 核心工具类

### `LogManager`（`DownKyi.Core.Logging`）

基于异步文件的日志记录器，使用由后台线程消费的 `ConcurrentQueue`。

```csharp
LogManager.Debug(Tag, "消息");
LogManager.Info(Tag, "消息");
LogManager.Warning(Tag, "消息");
LogManager.Error(Tag, exception);
```

日志文件写入 `StorageManager` 解析的应用数据目录。

### `Debugging.Console`（`DownKyi.Core.Utils.Debugging`）

仅用于调试的控制台封装，Release 构建中输出被抑制。使用该类型的文件须设置别名以避免与 `System.Console` 冲突：

```csharp
using Console = DownKyi.Core.Utils.Debugging.Console;
```

使用方式：

```csharp
Console.PrintLine("value: {0}", someValue);
```

### `SettingsManager`（`DownKyi.Core.Settings`）

将设置持久化到 JSON 文件的单例，按功能区域拆分为 partial 类：

- `SettingsManager.cs` — 核心、加载/保存
- `SettingsManager.Basic.cs` — 基本应用设置
- `SettingsManager.Network.cs` — 代理、Cookie
- `SettingsManager.Video.cs` — 画质、编码偏好
- `SettingsManager.Danmaku.cs` — 弹幕选项
- `SettingsManager.UserInfo.cs` — 已登录用户
- `SettingsManager.About.cs` — 版本信息
- `SettingsManager.WindowSetting.cs` — 窗口大小/位置

访问方式：`SettingsManager.GetInstance().GetXxx()` / `SetXxx(value)`。

### `StorageManager`（`DownKyi.Core.Storage`）

解析标准路径（应用数据、日志、临时文件、下载根目录）。路径解析始终使用 `StorageManager`，禁止硬编码路径。

### `WebClient`（`DownKyi.Core.BiliApi`）

所有 Bilibili API 调用的单例 `HttpClient` 封装，处理 Cookie 注入、User-Agent、重试和 JSON 反序列化。请使用静态辅助方法（`GetObject<T>`、`PostObject<T>`），而非直接构造 `HttpClient`。

---

## 包管理

本仓库使用**中央包管理（CPM）**。所有 NuGet 包版本在 `Directory.Packages.props` 中统一声明，各 `.csproj` 文件引用包时**不加** `Version` 属性：

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />

<!-- DownKyi.Core.csproj -->
<PackageReference Include="Newtonsoft.Json" />
```

添加新包时：
1. 在 `Directory.Packages.props` 中添加 `<PackageVersion Include="..." Version="..." />`。
2. 在对应的 `.csproj` 中添加 `<PackageReference Include="..." />`（不含版本号）。
