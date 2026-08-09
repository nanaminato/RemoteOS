# RemoteOS 项目说明文档

> 本文档描述 RemoteOS 当前实现状态：Solution 结构、项目列表、代码地图、当前实现进度、开发状态。
>
> - 架构设计原则见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)
> - 用户 Workspace 模型见 [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)
> - 登录与身份模型见 [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md)
> - 安全设计见 [`RemoteOS.Security.md`](./RemoteOS.Security.md)
> - 桌面外壳与模态对话框见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)
> - 文件管理器见 [`RemoteOS.Explorer.md`](./RemoteOS.Explorer.md)
> - 浏览器见 [`RemoteOS.Browser.md`](./RemoteOS.Browser.md)
> - 设置中心见 [`RemoteOS.Settings.md`](./RemoteOS.Settings.md)
> - 网络检查器设计见 [`RemoteOS.NetworkInspector.md`](./RemoteOS.NetworkInspector.md)
> - 任务管理器见 [`RemoteOS.TaskManager.md`](./RemoteOS.TaskManager.md)
> - 服务端持久化见 [`RemoteOS.Storage.md`](./RemoteOS.Storage.md)
> 当文档冲突时：本文档代表**当前代码实现**，Architecture 文档代表**设计原则**。

---

## 1. RemoteOS 简介

RemoteOS 是一个**云原生桌面操作系统**。

- **Client 端**：基于 Avalonia 的跨平台桌面 Shell，提供 Desktop、Window Manager、Application Runtime、Application SDK。
- **Server 端**：跨平台运行于 **Ubuntu（Linux）** 与 **Windows Server**，复用宿主 OS 用户与权限体系，提供 Workspace、Storage、Sync、Remote Runtime、Compute 能力。
- **主场景**：个人服务器、小型团队服务器的桌面化管理。

RemoteOS 采用状态同步模式（非像素流）：Client 本地渲染 UI，与 Server 同步状态/数据/命令。

---

## 2. 当前开发阶段

本地 RemoteOS Shell 已完成（Desktop、Window Manager、Application Runtime、Application SDK、内置应用 Welcome/Notebook/Code Editor/Image Viewer/Settings 等）。

桌面外壳已增强：宿主窗口控制（标题栏拖动 / 8 向 resize / 最小化·最大化·关闭 / 全屏）、mstsc 风格连接栏（全屏切换、固定与自动隐藏、连接信息、关闭连接 = 登出）、可复用模态对话框机制（`AppContext.ShowDialogAsync`，支持嵌套与任意结果类型）。详见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)。

内置终端应用已落地（Remote Mode）：通过 NuGet 包 `RoyalApps.RoyalTerminal.Avalonia` 引入 `TerminalControl`，嵌入 `RemoteWindow`；认证后经 SignalR Hub 连接 Server 端 PTY（哑中继），VT 渲染在客户端完成；未登录时回退本地 PTY。输入焦点问题已修复（`Focusable=true` + 延迟聚焦）。详见 [`RemoteOS.Terminal.md`](./RemoteOS.Terminal.md)。

内置文件管理器已落地（RemoteExplorer）：UI 移植自 Jaya File Manager（BSD-3），导航树 + Explorer 网格 + 地址栏 + 工具栏 + 状态栏；所有文件操作经 Server 端 REST API（`/api/v1/files/*`）执行，复用宿主 OS 用户/权限（不另建 ACL）；支持浏览、新建文件夹/删除/重命名/复制/移动/上传/下载、文件/目录属性查看（Linux POSIX 权限编辑），以及按扩展名声明进行默认打开或“打开方式”。详见 [`RemoteOS.Explorer.md`](./RemoteOS.Explorer.md)。

内置浏览器已落地（RemoteBrowser）：基于 NuGet 包 `Avalonia.Controls.WebView` 12.0.1 的 `NativeWebView`（平台原生引擎：Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），网页内容走客户端网络渲染；书签与历史记录经 Server 端 REST API（`/api/v1/browser/*`）持久化（按用户隔离，EF Core+SQLite）；浏览器偏好（`BrowserSettings`）随 Workspace 持久化控制本地端口映射开关；**本地端口映射**开启后 `localhost`/`127.0.0.1` 导航经 RemoteOS 鉴权通道转发到服务端 loopback（仅 loopback，非通用代理；JWT 换 HttpOnly cookie 鉴权）；UI 含顶部工具栏（后退/前进/刷新/停止/主页/加入·删除书签/侧边栏切换/本地端口映射开关）+ 地址栏 + 状态栏 + 左侧边栏双标签页（书签 / 历史，支持双击导航、单条删除、清空全部）。详见 [`RemoteOS.Browser.md`](./RemoteOS.Browser.md)。

内置设置中心已落地（RemoteSettings）：Windows 11 / GNOME 风格，5 个分类页（系统 / 个性化 / 时间和语言 / 网络 / 应用）。用户偏好（壁纸 / 主题 / 时间格式 / 日期格式 / 语言 / 区域 / 默认程序）经 Server 端 REST API（`/api/v1/workspaces/{id}/preferences`）持久化到 Workspace（`OwnsOne + ToJson` 单列 JSON，多设备共享）；登录时 `PreferencesSync` 自动加载应用到桌面外壳（壁纸 / 任务栏底色 / 时钟格式即时生效），设置应用编辑后防抖 300ms 保存。宿主 OS 级设置（时区 / 网卡）只读展示（硬约束「权限提升委托宿主 OS」）。详见 [`RemoteOS.Settings.md`](./RemoteOS.Settings.md)。

内置任务管理器已落地（RemoteTaskManager）：参考 Windows 任务管理器 / GNOME 系统监视器，性能 / 进程双标签页。性能页实时展示 CPU（整机 + 每核 + 60 采样柱状图）/ 内存 / 磁盘 / 网络 / GPU（nvidia-smi）/ 运行时间；进程页列出当前可见进程，按名称/PID/用户过滤，可结束任务（权限不足提示需在宿主 OS 提权）。数据经 Server 端 REST API（`/api/v1/system/*`）拉取，服务端 `ISystemMetricsProvider` 跨平台采集（Linux 读 `/proc`、Windows 走 P/Invoke `GetSystemTimes`/`GlobalMemoryStatusEx`），以宿主 OS 进程身份执行、不持久化。详见 [`RemoteOS.TaskManager.md`](./RemoteOS.TaskManager.md)。

系统采用**渐进式开发**——在本地 Shell 基础上逐步完善服务端能力：登录与身份、Workspace、安全、云同步、Storage、Remote Runtime 等。各能力的当前状态见 §8。

---

## 3. Solution Structure

`RemoteOS.sln` 当前包含以下项目：

```
Client/
    RemoteOS.Client              桌面 Shell + 内置应用（类库）
    RemoteOS.Client.Desktop      平台入口（WinExe）
Framework/
    RemoteOS.Core                平台无关原语与类型
    RemoteOS.UI                  Avalonia 共享主题/样式
    RemoteOS.WindowManager       窗口管理器 + RemoteWindow 控件
    RemoteOS.App.SDK             应用开发面（AppContext / IRemoteApplication）
    RemoteOS.Runtime             应用运行时（ApplicationManager）
Shared/
    RemoteOS.Protocol            通信协议契约（Common/Identity/Workspace/Desktop/Files/Hubs，已完整定义）
RemoteOS.Server/                 服务端（ASP.NET Core，跨平台，已实现 auth 端点）
Windows Server Test/             跨平台能力验证测试床（原生 API 探针）
```

---

## 4. 项目职责

### 4.1 RemoteOS.Client.Desktop

- **类型**：Executable (WinExe)
- **定位**：RemoteOS 平台启动入口，类似 Windows Boot Loader / Desktop Entry。
- **职责**：Avalonia AppBuilder、平台初始化、字体配置、日志配置、启动 `RemoteOS.Client`。
- **不包含**：Shell 逻辑、应用逻辑、窗口逻辑。

### 4.2 RemoteOS.Client

- **类型**：Class Library
- **定位**：RemoteOS Shell，类似 `explorer.exe`。
- **职责**：Desktop、Taskbar、StartMenu、MainWindow、Shell 生命周期。
- **包含内置应用**：Welcome、Notebook、Code Editor、Image Viewer、Settings、Terminal、Explorer、Browser、TaskManager。
- **系统启动时装配**：`WindowManager`、`ApplicationManager`、Shell Services。

### 4.3 RemoteOS.Core

- **定位**：基础抽象。所有模块依赖 Core。
- **包含**：
  - **Window Model**：`WindowId`、`WindowInfo`、`WindowState`
  - **Application Model**：`AppId`、`ApplicationManifest`、`ApplicationInfo`
  - **Geometry**：`Point`、`Size`、`Rect`
- **要求**：Core 必须保持纯净。禁止引用 Avalonia、Network、Database。

### 4.4 RemoteOS.UI

- **定位**：RemoteOS UI 组件库。
- **职责**：Theme、Style、Control Template。
- **目标**：统一 Windows 11 风格视觉。
- **包含**：Button Style、TextBox Style、List Style、Window Style。

### 4.5 RemoteOS.WindowManager

- **定位**：RemoteOS 窗口系统，负责模拟操作系统窗口管理。
- **核心架构**：

  ```text
  WindowManager
      |
  RemoteWindow
      |
  Avalonia Control
  ```

- **职责**：创建窗口、关闭窗口、移动、Resize、Focus、Minimize、Maximize、Z Order、Taskbar State、**模态对话框（`ShowDialogAsync` + `ModalDialog<TResult>` + owner 局部遮罩）**。详见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md) §3。
- **窗口创建流程**：

  ```text
  Application Launch
      |
  AppContext.ShowWindow
      |
  WindowManager.Create
      |
  RemoteWindow
  ```

### 4.6 RemoteOS.App.SDK

- **定位**：RemoteOS 应用开发接口，类似 Windows SDK / Android SDK。
- **提供**：
  - **Window API**（已实现）：`AppContext.ShowWindow()`
  - **Modal Dialog API**（已实现）：`AppContext.ShowDialogAsync<TResult>(owner, title, contentFactory)` — 可复用、可嵌套、任意结果类型，详见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md) §3
  - **Storage API**（规划）：`Storage.Save()` / `Storage.Load()`
  - **Sync API**（规划）：`Sync.Push()` / `Sync.Pull()`
  - **Remote API**（规划）：`RemoteClient.Execute()`
- **应用接入方式**：实现 `IRemoteApplication` 或继承 `RemoteApplicationBase`。

### 4.7 RemoteOS.Runtime

- **定位**：应用运行时。RemoteOS Application 不是普通 exe。
- **职责**：Application Registry、Application Loading、Application Lifecycle。
- **流程**：

  ```text
  Desktop Icon
      |
  ApplicationManager.Launch
      |
  Create AppContext
      |
  IRemoteApplication.Activate
      |
  Create Window
  ```

- **不负责**：Window Algorithm、UI Rendering。

### 4.8 RemoteOS.Protocol

- **定位**：Client↔Server 通信契约层。已完整定义全部 DTO/路由/Hub 契约（Common/Identity/Workspace/Desktop/Files/Hubs）。
- **包含**：DTO（sealed record + `[property: JsonPropertyName]`）、API Contract（`*ApiRoutes` 路由常量）、SignalR Hub 接口（`IWorkspaceHubClient` / `ITerminalHubClient` + Methods/Events 常量）、序列化约定。Client Proxy 实现位于 `RemoteOS.Client`，Hub/端点实现位于 `RemoteOS.Server`。详见 [`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md)。
- **规则**：所有 Client / Server 通信必须经过 Protocol。禁止业务代码直接调用 HTTP / WebSocket。Protocol 程序集零 PackageReference。

### 4.9 RemoteOS.Server

- **定位**：RemoteOS Cloud Backend，**跨平台运行于 Ubuntu / Windows Server**。已实现 auth 端点（login/refresh/logout/me）+ JWT + `IIdentityProvider`（`WindowsLogonProvider` 迁移自测试床，`LinuxPamProvider` 占位）+ 持久化仓储（EF Core + SQLite，User/Workspace/Device，含 TerminalSettings / BrowserSettings / Preferences）+ 文件管理端点（`/api/v1/files/*`：drives/special/list/info/download/content/properties/permissions/directory/delete/rename/move/copy/upload，`IFileService` + `LocalFileService` 以宿主 OS 进程身份执行 IO，复用宿主用户/权限）+ 浏览器端点（`/api/v1/browser/*`，按用户隔离书签/历史 + `BrowserSettings` 持久化 + 本地端口映射 loopback 转发）+ Workspace 偏好端点（`/api/v1/workspaces/{id}/preferences`，壁纸/主题/时间格式/语言/区域/默认程序）+ 系统监控端点（`/api/v1/system/*`：metrics/processes/processes/{id}，`ISystemMetricsProvider` 跨平台采集 CPU/内存/磁盘/网络/GPU + 进程列表，不持久化）。详见 [`RemoteOS.Login.md`](./RemoteOS.Login.md) / [`RemoteOS.Explorer.md`](./RemoteOS.Explorer.md) / [`RemoteOS.Browser.md`](./RemoteOS.Browser.md) / [`RemoteOS.Settings.md`](./RemoteOS.Settings.md) / [`RemoteOS.TaskManager.md`](./RemoteOS.TaskManager.md)。
- **负责**：Authentication、Identity Mapping（跨平台 OS 用户集成）、Workspace、Session、Device、Storage、Sync、Remote Runtime、Compute、Security Integration。
- **架构**：单一代码库 + OS 抽象层（`IIdentityProvider` / `ISystemMetricsProvider` 等接口 + Linux/Windows 各自实现），平台差异封装在抽象之后。
- **持久化**：User/Workspace(含 TerminalSettings/BrowserSettings/Preferences)/Device/Bookmark/HistoryEntry 落 SQLite（EF Core），Session/刷新令牌/PTY 进程维持内存（各有语义理由）。详见 [`RemoteOS.Storage.md`](./RemoteOS.Storage.md)。
- **不负责**：UI Rendering、Window Management、Screen Streaming。
- **详见**：[`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md)、[`RemoteOS.Security.md`](./RemoteOS.Security.md)、[`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)、[`RemoteOS.Storage.md`](./RemoteOS.Storage.md)

### 4.10 Windows Server Test

- **类型**：Executable (Console)
- **定位**：跨平台能力验证测试床。在把原生 OS 能力集成进 Server 之前，先用独立控制台程序验证 API 调用正确性。
- **已验证**：Windows 凭据验证（Win32 `LogonUser` API，支持本地账户 `MACHINE\user` 与域账户 `user@domain`，含错误码映射）。
- **职责**：为 Server 跨平台支持（Ubuntu + Windows Server）提供原生 API 探针——认证、文件、进程、服务管理等能力的本机验证。
- **不包含**：生产代码。验证通过的能力后续迁移到 `RemoteOS.Server` 的 OS 抽象层实现中。

---

## 5. Application 开发模型

RemoteOS Application 结构：

```text
Application Package
├── Manifest
├── UI
├── Logic
├── State Manager
└── Remote Connector
```

- **当前**：Manifest 由代码创建。
- **未来**：支持应用包加载。

---

## 6. 内置应用规划

| 应用 | 用途 | 状态 |
|------|------|------|
| **Welcome** | 验证 Runtime、WindowManager | 已实现 |
| **Notebook** | 远端文本文件编辑（编码打开与保存） | 已实现 |
| **Code Editor** | 远端代码文件编辑（语法高亮） | 已实现 |
| **Image Viewer** | 常见远端图片文件浏览（缩放与滚动） | 已实现 |
| **Settings** | 系统设置中心（5 分类页，偏好持久化到 Workspace） | 已实现（壁纸/主题/时间格式/语言/区域/默认程序 + 服务端同步） |
| **Terminal** | 远端终端（RoyalTerminal + SignalR Remote Mode） | 已实现（Remote Mode + Local 回退） |
| **Explorer** | 远端文件管理器（Jaya UI 移植 + REST API + 宿主 OS 权限复用） | 已实现（浏览、基本操作、文件打开方式、属性与 Linux 权限编辑） |
| **Browser** | 内置浏览器（Avalonia.Controls.WebView + 书签/历史持久化到 Server + 本地端口映射） | 已实现（导航 + 书签 + 历史 + 浏览器偏好 + 本地端口映射） |
| **TaskManager** | 远端宿主 OS 任务管理器（CPU/内存/磁盘/网络/GPU 占用 + 进程列表，可结束任务） | 已实现（性能页 + 进程页，跨平台指标采集） |
| **DockerManager** | 本机 Docker Engine 的检测/安装引导、容器、镜像、Stack、网络与卷管理 | 设计中（详见 [`RemoteOS.DockerManager.md`](./RemoteOS.DockerManager.md)） |
| **ProcessGuardian** | 受守护工作负载、健康检查、自动恢复、日志与原生服务管理 | 设计中（详见 [`RemoteOS.ProcessGuardian.md`](./RemoteOS.ProcessGuardian.md)） |

---

## 7. 未来应用规划

### RemoteBrowser

- **定位**：不是远程浏览器。网页内容走客户端网络由平台原生引擎渲染（Win=WebView2/macOS=WKWebView/Linux=WebKitGTK）。
- **结构**：`RemoteBrowser → RemoteWindow → NativeWebView (Avalonia.Controls.WebView 12.0.1)`。
- **网页**：本地加载。
- **同步到 Server**：History、Bookmark（已实现，按用户隔离，EF Core+SQLite 持久化）；BrowserSettings（已实现，随 Workspace 持久化，控制本地端口映射开关）；Cookie/Extension Config（未实现）。
- **本地端口映射**（已实现）：开启后客户端浏览器导航 `localhost:port` / `127.0.0.1:port` 时经 RemoteOS 鉴权通道转发到**服务端 loopback**——让用户在远端桌面里访问宿主 OS 上运行的 Web 服务。仅 loopback，非通用代理；JWT 换 HttpOnly cookie 鉴权（不暴露给脚本）。
- **已实现**：导航（后退/前进/刷新/停止/主页/地址栏）+ 书签（加入/删除/侧边栏双击导航/清空全部）+ 历史（自动记录访问/侧边栏双击导航/单条删除/清空全部）+ 浏览器偏好持久化 + 本地端口映射（loopback → 服务端）；JWT via IAuthSession；未登录弹提示窗。详见 [`RemoteOS.Browser.md`](./RemoteOS.Browser.md)。

### RemoteTaskManager

- **定位**：远端宿主 OS 任务管理器，参考 Windows 任务管理器 / GNOME 系统监视器。
- **结构**：`TaskManagerApp → RemoteWindow → TaskManagerMainView`（性能 / 进程两个标签页）。
- **数据源**：Server 端 `ISystemMetricsProvider` 以宿主 OS 进程身份实时采集（CPU/内存/磁盘/网络/GPU + 进程列表），**不持久化**（每次请求当下快照）。
- **跨平台抽象**：与 `IIdentityProvider` 同模式——`WindowsMetricsProvider`（GetSystemTimes + GlobalMemoryStatusEx）/ `LinuxMetricsProvider`（/proc/stat + /proc/meminfo + /proc/[pid]/status），平台差异封装在 Provider 之后。
- **已实现**：性能页（CPU 整机+每核+柱状图 / 内存柱状图 / 磁盘 / 网络速率 / GPU nvidia-smi / 运行时间，2s 自动刷新）+ 进程页（列表 + 按名称/PID/用户过滤 + 结束任务，权限不足提示需在宿主 OS 提权）；JWT via IAuthSession；未登录弹提示窗。详见 [`RemoteOS.TaskManager.md`](./RemoteOS.TaskManager.md)。

### RemoteTerminal

支持两种模式：

- **Remote Mode**：运行于 Server。**已实现**——PTY 运行于 `RemoteOS.Server`，经 SignalR Hub（`/hubs/terminals`）流式传输到 Client。Server 端是 PTY 哑中继（只转发字节），VT 渲染在客户端 `TerminalControl` 完成。JWT 通过 SignalR `AccessTokenProvider` 鉴权。详见 [`RemoteOS.Terminal.md`](./RemoteOS.Terminal.md)。
- **Local Mode**：运行于 Client，例如 PowerShell / CMD / Bash。**已实现**（回退）——未登录时自动回退到本地 PTY。

  ```text
  TerminalControl (Client)
      |
  SignalRTransport (ITerminalTransport)
      |
  SignalR Hub (/hubs/terminals, JWT)
      |
  TerminalHub → IPty (ConPTY/forkpty)
      |
  Shell
  ```

  > RoyalTerminal 传输抽象（`ITerminalTransport`）是传输方式无关的，SignalR 与裸 WebSocket 均可行。本项目选择 SignalR（自动重连 + JWT + 强类型 Hub 契约），不引入裸 WebSocket 端点。

### RemoteExplorer

- **定位**：远程文件管理，**不是**远程桌面文件浏览。
- **结构**：`Explorer UI → RemoteServer API → Remote File System`。
- **已实现**：UI 移植自 Jaya File Manager（BSD-3），导航树（懒加载）+ Explorer 网格 + 地址栏 + 工具栏 + 状态栏。所有文件操作经 Server 端 REST API（`/api/v1/files/*`）执行，Server 以宿主 OS 进程身份执行 `System.IO`，复用宿主用户/权限（不另建 ACL——project_memory 硬约束）。支持浏览、新建文件夹/删除/重命名/复制/移动/上传/下载、文件/目录属性查看（Linux 可编辑 POSIX 权限），以及依据应用 manifest 扩展名声明的默认打开与“打开方式”；危险操作（删除）弹确认对话框。JWT 复用 `IAuthSession`。详见 [`RemoteOS.Explorer.md`](./RemoteOS.Explorer.md)。

---

## 8. 已完成里程碑

| 阶段 | 内容 | 状态 |
|------|------|------|
| 阶段 0 | Desktop / Wallpaper / Icon / Taskbar / WindowManager | 完成（+ 宿主窗口控制 / mstsc 连接栏 / 模态对话框，见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)） |
| 阶段 1 | Runtime / App.SDK / Launch App / Create Window / Modal Dialog | 完成 |
| 阶段 2 | RemoteBrowser / RemoteTerminal / RemoteExplorer / RemoteTaskManager | 完成（RemoteTerminal Local+Remote Mode；RemoteExplorer 浏览、文件打开方式、属性与基本操作；RemoteBrowser 导航+书签+历史+浏览器偏好+本地端口映射；RemoteTaskManager 性能页+进程页，跨平台指标采集） |
| 阶段 3 | RemoteServer：Account / Workspace / Sync / Storage / Remote State | 完成（登录模块；服务端 SQLite 持久化——User/Workspace(含 TerminalSettings/BrowserSettings/Preferences)/Device/Bookmark/HistoryEntry 落库，见 [`RemoteOS.Storage.md`](./RemoteOS.Storage.md)；设置中心——偏好持久化到 Workspace + 多设备同步，见 [`RemoteOS.Settings.md`](./RemoteOS.Settings.md)） |

---

## 9. 当前开发重点

开发顺序：

1. `RemoteOS.Client`
2. `RemoteOS.WindowManager`
3. `RemoteOS.Core`
4. `RemoteOS.Runtime`
5. `RemoteOS.App.SDK`

---

## 10. 后续逐步实现

本地 Shell 已就绪，系统按以下方向逐步丰富（设计先行，再落地代码）：

- **登录与身份**（Windows LogonUser 已实现，Linux PAM 占位）— 已完成，见 [`RemoteOS.Login.md`](./RemoteOS.Login.md)；设计原则见 [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md)
- **安全设计**（sudo / 权限 / 危险操作确认）— 见 [`RemoteOS.Security.md`](./RemoteOS.Security.md)
- **Workspace / Session / Device 多设备模型** — 见 [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)
- **云同步、Storage、Remote Runtime、Compute** — 见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)
- **RemoteBrowser / RemoteTerminal / RemoteExplorer** 内置应用

实现节奏：每个能力先完成设计文档，再逐步实现代码。

---

## 11. 开发约束

修改代码时必须保持：

| 关注点 | 归属 |
|--------|------|
| 窗口逻辑 | `RemoteOS.WindowManager` |
| 应用生命周期 | `RemoteOS.Runtime` |
| 系统入口 / Shell | `RemoteOS.Client` |
| 网络通信 | `RemoteOS.Protocol` |

---

## 12. AI Agent 快速理解

修改 RemoteOS 代码前必须理解：

```text
RemoteOS            = Operating System Shell
RemoteOS.Client     = Desktop Shell
RemoteOS.WindowManager = Window System
RemoteOS.Runtime    = Application Runtime
RemoteOS.Server     = Cloud Backend
```

- **不要**将 RemoteOS 实现为 Remote Desktop Tool 或 Web Management Dashboard。
- **正确方向**：Application State + Local Rendering + Cloud Capability。

---

## 13. 文档索引

| 文档 | 用途 |
|------|------|
| [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md) | 模块设计、依赖关系、架构原则 |
| [`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md) | 通信协议契约层、REST/SignalR、序列化约定 |
| [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md) | User / Workspace / Session / Device、多设备、云桌面状态 |
| [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md) | 登录系统、Linux 用户集成、身份模型 |
| [`RemoteOS.Login.md`](./RemoteOS.Login.md) | 登录模块：mstsc 风格登录窗、auth 端点、JWT、IIdentityProvider、错误处理 |
| [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md) | 桌面外壳：宿主窗口控制、mstsc 连接栏、模态对话框机制 |
| [`RemoteOS.Terminal.md`](./RemoteOS.Terminal.md) | 终端应用：RoyalTerminal 集成、Local Mode PTY、会话生命周期、Remote Mode 演进 |
| [`RemoteOS.Explorer.md`](./RemoteOS.Explorer.md) | 文件管理器：Jaya UI 移植、REST API、宿主 OS 权限复用、文件操作、对话框集成 |
| [`RemoteOS.Browser.md`](./RemoteOS.Browser.md) | 内置浏览器：Avalonia.Controls.WebView、NativeWebView、书签/历史 REST API、BrowserSettings 持久化、本地端口映射（loopback → 服务端）、按用户隔离持久化 |
| [`RemoteOS.Settings.md`](./RemoteOS.Settings.md) | 设置中心：5 分类页、Workspace 偏好持久化、PreferencesSync 多设备同步、默认程序映射 |
| [`RemoteOS.NetworkInspector.md`](./RemoteOS.NetworkInspector.md) | 网络检查器：外部 UI + 宿主采集、REST/SignalR 诊断、内存/隐私边界、权限与国际化设计 |
| [`RemoteOS.TaskManager.md`](./RemoteOS.TaskManager.md) | 任务管理器：性能/进程双标签页、跨平台 ISystemMetricsProvider（Linux /proc + Windows P/Invoke）、CPU 差分、结束进程不自动提权 |
| [`RemoteOS.DockerManager.md`](./RemoteOS.DockerManager.md) | Docker 管理器：本机 Engine、安装预检与引导、容器/镜像/Stack/网络/卷、权限与审计设计 |
| [`RemoteOS.ProcessGuardian.md`](./RemoteOS.ProcessGuardian.md) | 进程守护管理器：独立 Guardian Agent、守护定义、健康检查、重启策略、systemd/SCM 适配设计 |
| [`RemoteOS.BuiltInApplication.Conventions.md`](./RemoteOS.BuiltInApplication.Conventions.md) | 所有内置应用的设计先行、国际化、Windows + Ubuntu、协议、安全与质量约束 |
| [`RemoteOS.Storage.md`](./RemoteOS.Storage.md) | 服务端持久化：EF Core + SQLite、持久化范围、表结构、TerminalSettings/BrowserSettings/Preferences JSON 列、建库策略 |
| [`RemoteOS.Security.md`](./RemoteOS.Security.md) | 安全设计、sudo、权限提升、危险操作确认 |
| [`RemoteOS.md`](./RemoteOS.md) | 项目结构、代码位置、当前进度 |
