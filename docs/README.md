# RemoteOS 文档索引与项目说明

> 本文档描述 RemoteOS 当前实现状态：Solution 结构、项目列表、代码地图、当前实现进度、开发状态。
>
> - 架构设计原则见 [`RemoteOS.Architecture.md`](./architecture/RemoteOS.Architecture.md)
> - 应用启动 URI 与窗口实例策略见 [`RemoteOS.ApplicationActivation.md`](./architecture/RemoteOS.ApplicationActivation.md)
> - 用户 Workspace 模型见 [`RemoteOS.Workspace.md`](./architecture/RemoteOS.Workspace.md)
> - 注册表与配置同步架构见 [`RemoteOS.Registry.md`](./architecture/RemoteOS.Registry.md)（设计中）
> - 登录与身份模型见 [`RemoteOS.Authentication.md`](./platform/RemoteOS.Authentication.md)
> - 安全设计见 [`RemoteOS.Security.md`](./platform/RemoteOS.Security.md)
> - 桌面外壳与模态对话框见 [`RemoteOS.Desktop.md`](./desktop/RemoteOS.Desktop.md)
> - 文件管理器见 [`RemoteOS.Explorer.md`](./applications/RemoteOS.Explorer.md)
> - 浏览器见 [`RemoteOS.Browser.md`](./applications/RemoteOS.Browser.md)
> - 设置中心见 [`RemoteOS.Settings.md`](./desktop/RemoteOS.Settings.md)
> - 全局主题与配色系统设计见 [`RemoteOS.Theming.md`](./desktop/RemoteOS.Theming.md)
> - 应用私有配置存储见 [`RemoteOS.AppSettings.md`](./development/RemoteOS.AppSettings.md)
> - 网络检查器设计见 [`RemoteOS.NetworkInspector.md`](./applications/RemoteOS.NetworkInspector.md)
> - 任务管理器见 [`RemoteOS.TaskManager.md`](./applications/RemoteOS.TaskManager.md)
> - 任务管理器性能采集重写方案（后续 Goal 执行基线）见 [`RemoteOS.TaskManager.Rewrite.md`](./applications/RemoteOS.TaskManager.Rewrite.md)
> - FRP 内网穿透的 Goal 执行基线见 [`RemoteOS.FRP_Integration.Goal.md`](./applications/RemoteOS.FRP_Integration.Goal.md)；架构与安全设计见 [`RemoteOS.FRP_Integration.Design.md`](./applications/RemoteOS.FRP_Integration.Design.md)，当前实现与运维边界见 [`RemoteOS.FRP_Integration.Implementation.md`](./applications/RemoteOS.FRP_Integration.Implementation.md)
> - Docker 管理器见 [`RemoteOS.DockerManager.md`](./applications/RemoteOS.DockerManager.md)
> - 证书管理器见 [`RemoteOS.CertificateManager.md`](./applications/RemoteOS.CertificateManager.md)
> - Web Server 管理器 / Nginx 集成设计中，见 [`RemoteOS.WebServerManager.Design.md`](./applications/RemoteOS.WebServerManager.Design.md)
> - 进程守护见 [`RemoteOS.ProcessGuardian.md`](./applications/RemoteOS.ProcessGuardian.md)
> - Git 客户端见 [`RemoteOS.GitClient.md`](./applications/RemoteOS.GitClient.md)
> - 服务端持久化见 [`RemoteOS.Storage.md`](./platform/RemoteOS.Storage.md)
> - 开发者指南见 [`RemoteOS.Develop.md`](./development/RemoteOS.Develop.md)
> - 开发模式与扩展见 [`RemoteOS.DeveloperMode.md`](./development/RemoteOS.DeveloperMode.md)
> 当文档冲突时：本文档代表**当前代码实现**，Architecture 文档代表**设计原则**。

---

## 文档目录

| 目录 | 内容 |
|------|------|
| [`architecture/`](./architecture/) | 架构原则、通信协议与 Workspace 运行模型 |
| [`platform/`](./platform/) | 身份认证、登录、安全与服务端持久化 |
| [`desktop/`](./desktop/) | 桌面外壳、设置与本地化 |
| [`applications/`](./applications/) | 各内置应用的设计与实现说明 |
| [`development/`](./development/) | 开发调试、开发者模式与应用扩展规范 |

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

应用启动与跨应用导航已具备首个可运行基础：Shell 解析受控 `remoteos://` URI，Settings 支持直达个性化和指定应用权限页；RemoteExplorer 通过此入口打开文件。`ApplicationManifest.InstancePolicy` 可声明多窗口或单窗口，Settings/任务管理器/端口转发/防火墙/进程守护/Docker 为单窗口，Notebook 与 Code Editor 明确支持多窗口。详见 [`RemoteOS.ApplicationActivation.md`](./architecture/RemoteOS.ApplicationActivation.md)。

桌面外壳已增强：宿主窗口控制（标题栏拖动 / 8 向 resize / 最小化·最大化·关闭 / 全屏）、mstsc 风格连接栏（全屏切换、固定与自动隐藏、连接信息、关闭连接 = 登出）、可复用模态对话框机制（`AppContext.ShowDialogAsync`，支持嵌套与任意结果类型）。详见 [`RemoteOS.Desktop.md`](./desktop/RemoteOS.Desktop.md)。

内置终端应用已落地（Remote Mode）：通过 NuGet 包 `RoyalApps.RoyalTerminal.Avalonia` 引入 `TerminalControl`，嵌入 `RemoteWindow`；认证后经 SignalR Hub 连接 Server 端 PTY（哑中继），VT 渲染在客户端完成；未登录时回退本地 PTY。输入焦点问题已修复（`Focusable=true` + 延迟聚焦）。详见 [`RemoteOS.Terminal.md`](./applications/RemoteOS.Terminal.md)。

内置文件管理器已落地（RemoteExplorer）：UI 移植自 Jaya File Manager（BSD-3），导航树 + Explorer 网格 + 地址栏 + 工具栏 + 状态栏；所有文件操作经 Server 端 REST API（`/api/v1/files/*`）执行，复用宿主 OS 用户/权限（不另建 ACL）；支持浏览、新建文件夹/删除/重命名/复制/移动/上传/下载、文件/目录属性查看（Linux POSIX 权限编辑），以及按扩展名声明进行默认打开或“打开方式”。详见 [`RemoteOS.Explorer.md`](./applications/RemoteOS.Explorer.md)。

内置浏览器已落地（RemoteBrowser）：基于 NuGet 包 `Avalonia.Controls.WebView` 12.0.1 的 `NativeWebView`（平台原生引擎：Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），网页内容走客户端网络渲染；书签与历史记录经 Server 端 REST API（`/api/v1/browser/*`）持久化（按用户隔离，EF Core+SQLite）；浏览器主页可在浏览器设置中同步，链接打开位置（内置浏览器或宿主机浏览器）在“设置 → 应用 → 远程浏览器”中同步。服务端 loopback URL 由新的本机 Port Forwarding 应用通过 `ssh -L` 映射为有效 localhost 链接；该应用只绑定 loopback，SSH 设置与活动隧道都不会同步。详见 [`RemoteOS.Browser.md`](./applications/RemoteOS.Browser.md) 与 [`RemoteOS.PortForwarding.md`](./applications/RemoteOS.PortForwarding.md)。

内置设置中心已落地（RemoteSettings）：Windows 11 / GNOME 风格，5 个分类页（系统 / 个性化 / 时间和语言 / 网络 / 应用）。用户偏好（壁纸 / 主题 / 时间格式 / 日期格式 / 语言 / 区域 / 默认程序）经 Server 端 REST API（`/api/v1/workspaces/{id}/preferences`）持久化到 Workspace（`OwnsOne + ToJson` 单列 JSON，多设备共享）；登录时 `PreferencesSync` 自动加载应用到桌面外壳（壁纸 / 任务栏底色 / 时钟格式即时生效），设置应用编辑后防抖 300ms 保存。宿主 OS 级设置（时区 / 网卡）只读展示（硬约束「权限提升委托宿主 OS」）。详见 [`RemoteOS.Settings.md`](./desktop/RemoteOS.Settings.md)。

内置任务管理器正在重写（RemoteTaskManager）：性能页改由 Server 端统一 1 秒采样器、60 秒内存历史与 SignalR（`/hubs/performance`）推送驱动；CPU/内存/文件系统/网络/磁盘 I/O 跨 Windows/Linux 统一建模，宿主机或服务身份不支持的能力会明确降级而非显示伪造数值。进程页使用独立低频采样与分页查询；结束进程仍不自动提权。旧 REST metrics 契约暂为兼容保留。详见 [`RemoteOS.TaskManager.Rewrite.md`](./applications/RemoteOS.TaskManager.Rewrite.md)。

内置 Docker 管理器已部分落地（RemoteDocker）：本机 Docker Engine 检测与状态展示、容器启停重启、Compose 校验/部署/停止；镜像、网络、卷管理功能设计中。Server 端通过 `IDockerEngineService` 调用 `docker` CLI，`IDockerComposeService` 处理 Compose 编排。详见 [`RemoteOS.DockerManager.md`](./applications/RemoteOS.DockerManager.md)。

内置进程守护已部分落地（ProcessGuardian）：独立 Guardian Agent 进程、本机认证 IPC（命名管道）、工作负载的声明持久化与启停重启；健康检查、日志广播、systemd/SCM 服务适配设计中。Client 端通过 SignalR Hub 订阅守护日志。详见 [`RemoteOS.ProcessGuardian.md`](./applications/RemoteOS.ProcessGuardian.md)。

内置防火墙应用已落地（Firewall）：仅 Linux Server + UFW，支持读取状态与编号规则、修改启用状态和默认策略、添加或删除经过结构化校验的规则。root 会话无需再次验证；其他用户每次变更均以其自身密码通过 PAM 一次性确认。Windows Server 不显示此应用。详见 [`RemoteOS.Firewall.md`](./applications/RemoteOS.Firewall.md)。

内置证书管理器已落地基础闭环：ACME 证书列表、申请前预检、异步申请/取消、续期、Kestrel 部署、吊销和删除；客户端使用概览/证书列表多页工作区，申请操作在可滚动的模态对话框中完成。DNS-01、Wildcard 与 IIS/Nginx/Apache 部署仍属后续阶段。Web Server 管理器仍为**设计中**，规划 Nginx 的发现、最小侵入集成和托管模式。

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
RemoteOS.Guardian.Agent/         进程守护独立进程（原生服务管理，命名管道 IPC）
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

- **职责**：创建窗口、关闭窗口、移动、Resize、Focus、Minimize、Maximize、Z Order、Taskbar State、**模态对话框（`ShowDialogAsync` + `ModalDialog<TResult>` + owner 局部遮罩）**。详见 [`RemoteOS.Desktop.md`](./desktop/RemoteOS.Desktop.md) §3。
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
  - **Modal Dialog API**（已实现）：`AppContext.ShowDialogAsync<TResult>(owner, title, contentFactory)` — 可复用、可嵌套、任意结果类型，详见 [`RemoteOS.Desktop.md`](./desktop/RemoteOS.Desktop.md) §3
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
- **包含**：DTO（sealed record + `[property: JsonPropertyName]`）、API Contract（`*ApiRoutes` 路由常量）、SignalR Hub 接口（`IWorkspaceHubClient` / `ITerminalHubClient` + Methods/Events 常量）、序列化约定。Client Proxy 实现位于 `RemoteOS.Client`，Hub/端点实现位于 `RemoteOS.Server`。详见 [`RemoteOS.Protocol.md`](./architecture/RemoteOS.Protocol.md)。
- **规则**：所有 Client / Server 通信必须经过 Protocol。禁止业务代码直接调用 HTTP / WebSocket。Protocol 程序集零 PackageReference。

### 4.9 RemoteOS.Server

- **定位**：RemoteOS Cloud Backend，**跨平台运行于 Ubuntu / Windows Server**。已实现 auth 端点（login/refresh/logout/me）+ JWT + `IIdentityProvider`（Windows LogonUser / Linux PAM + NSS）+ 持久化仓储（EF Core + SQLite，User/Workspace/Device，含 TerminalSettings / BrowserSettings / Preferences）+ 文件管理端点（`/api/v1/files/*`：drives/special/list/info/download/content/properties/permissions/directory/delete/rename/move/copy/upload，`IFileService` + `LocalFileService` 以宿主 OS 进程身份执行 IO，复用宿主用户/权限）+ 浏览器端点（`/api/v1/browser/*`，按用户隔离书签/历史 + `BrowserSettings` 持久化）+ Workspace 偏好端点（`/api/v1/workspaces/{id}/preferences`，壁纸/主题/时间格式/语言/区域/默认程序）+ 系统监控端点（`/api/v1/system/*`：metrics/processes/processes/{id}，`ISystemMetricsProvider` 跨平台采集 CPU/内存/磁盘/网络/GPU + 进程列表，不持久化）+ Docker 管理端点（`/api/v1/docker/*`：status/containers/images/stacks/networks/volumes，`IDockerEngineService` + `IDockerComposeService`，调用 `docker` CLI）+ 进程守护端点（`/api/v1/guardian/*` + SignalR Hub `/hubs/guardian-logs`，`IProcessGuardianService` 通过命名管道与 Guardian Agent IPC）。详见 [`RemoteOS.Login.md`](./platform/RemoteOS.Login.md) / [`RemoteOS.Explorer.md`](./applications/RemoteOS.Explorer.md) / [`RemoteOS.Browser.md`](./applications/RemoteOS.Browser.md) / [`RemoteOS.Settings.md`](./desktop/RemoteOS.Settings.md) / [`RemoteOS.TaskManager.md`](./applications/RemoteOS.TaskManager.md) / [`RemoteOS.DockerManager.md`](./applications/RemoteOS.DockerManager.md) / [`RemoteOS.ProcessGuardian.md`](./applications/RemoteOS.ProcessGuardian.md)。
- **负责**：Authentication、Identity Mapping（跨平台 OS 用户集成）、Workspace、Session、Device、Storage、Sync、Remote Runtime、Compute、Security Integration。
- **架构**：单一代码库 + OS 抽象层（`IIdentityProvider` / `ISystemMetricsProvider` 等接口 + Linux/Windows 各自实现），平台差异封装在抽象之后。
- **持久化**：User/Workspace(含 TerminalSettings/BrowserSettings/Preferences)/Device/Bookmark/HistoryEntry 落 SQLite（EF Core），Session/刷新令牌/PTY 进程维持内存（各有语义理由）。详见 [`RemoteOS.Storage.md`](./platform/RemoteOS.Storage.md)。
- **不负责**：UI Rendering、Window Management、Screen Streaming。
- **详见**：[`RemoteOS.Authentication.md`](./platform/RemoteOS.Authentication.md)、[`RemoteOS.Security.md`](./platform/RemoteOS.Security.md)、[`RemoteOS.Workspace.md`](./architecture/RemoteOS.Workspace.md)、[`RemoteOS.Storage.md`](./platform/RemoteOS.Storage.md)

### 4.10 Windows Server Test

- **类型**：Executable (Console)
- **定位**：跨平台能力验证测试床。在把原生 OS 能力集成进 Server 之前，先用独立控制台程序验证 API 调用正确性。
- **已验证**：Windows 凭据验证（Win32 `LogonUser` API，支持本地账户 `MACHINE\user` 与域账户 `user@domain`，含错误码映射）。
- **职责**：为 Server 跨平台支持（Ubuntu + Windows Server）提供原生 API 探针——认证、文件、进程、服务管理等能力的本机验证。
- **不包含**：生产代码。验证通过的能力后续迁移到 `RemoteOS.Server` 的 OS 抽象层实现中。

### 4.11 RemoteOS.Guardian.Agent

- **类型**：Executable (Console / Windows Service / systemd 守护进程)
- **定位**：独立于 Server 的受守护工作负载执行代理。以高权限运行，负责实际的进程守护、健康检查和自动恢复。
- **架构**：
  - `GuardianWorker`：主工作循环，监听来自 Server 的守护指令
  - `GuardianPipeServer`：命名管道 IPC 服务端，接收 Server 端 `IProcessGuardianService` 的指令
  - `WorkloadSupervisor`：工作负载生命周期管理（启动、监控、重启）
  - `ProtectedServerMonitor`：监控受保护的服务器进程健康状态
- **通信**：通过命名管道（`NamedPipeProcessGuardianService`）与 Server 端通信，本机认证确保只有合法进程可连接。
- **已实现**：独立进程骨架、命名管道通信、工作负载启停重启、状态持久化。
- **设计中**：健康检查、日志广播（SignalR Hub `/hubs/guardian-logs`）、Windows SCP 注册 / systemd 服务适配。
- **详见**：[`RemoteOS.ProcessGuardian.md`](./applications/RemoteOS.ProcessGuardian.md)

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
| **Code Editor** | 远端代码文件编辑（语法高亮、多文件夹工作区与多标签编辑） | 设计中（单文件编辑已实现） |
| **Image Viewer** | 常见远端图片文件浏览（缩放与滚动） | 已实现 |
| **Settings** | 系统设置中心（5 分类页，偏好持久化到 Workspace） | 已实现（壁纸/主题/时间格式/语言/区域/默认程序 + 服务端同步） |
| **Terminal** | 远端终端（RoyalTerminal + SignalR Remote Mode） | 已实现（Remote Mode + Local 回退） |
| **Explorer** | 远端文件管理器（Jaya UI 移植 + REST API + 宿主 OS 权限复用） | 已实现（浏览、基本操作、文件打开方式、属性与 Linux 权限编辑） |
| **Browser** | 内置浏览器（Avalonia.Controls.WebView + 书签/历史持久化到 Server） | 已实现（导航 + 书签 + 历史 + 浏览器偏好） |
| **TaskManager** | 远端宿主 OS 任务管理器（CPU/内存/磁盘/网络/GPU 占用 + 进程列表，可结束任务） | 已实现（性能页 + 进程页，跨平台指标采集） |
| **DockerManager** | 本机 Docker Engine 的检测/安装引导、容器、镜像、Stack、网络与卷管理 | 已实现（状态和资源只读列表、容器启停重启、Compose 校验/部署/停止；其余功能设计中，详见 [`RemoteOS.DockerManager.md`](./applications/RemoteOS.DockerManager.md)） |
| **ProcessGuardian** | 受守护工作负载、健康检查、自动恢复、日志与原生服务管理 | 已实现（独立 Agent、本机认证 IPC、工作负载的声明持久化与启停重启；健康/日志/服务适配设计中，详见 [`RemoteOS.ProcessGuardian.md`](./applications/RemoteOS.ProcessGuardian.md)） |
| **CertificateManager** | 本机 ACME 证书申请、部署与续期 | 已实现（基础 UI、预检、申请/取消、续期、Kestrel 部署、吊销与删除；DNS-01、Wildcard 与其他 Web Server 部署设计中，详见 [`RemoteOS.CertificateManager.md`](./applications/RemoteOS.CertificateManager.md)） |
| **WebServerManager** | Nginx 发现、最小侵入集成与后续托管 | 设计中（尚未实现，详见 [`RemoteOS.WebServerManager.Design.md`](./applications/RemoteOS.WebServerManager.Design.md)） |
| **GitClient** | 远端宿主机 Git 仓库版本控制（分支切换/新建/删除、提交、拉取含冲突解决、推送、历史、Revert、Diff） | 设计中（参考 TortoiseGit，Server 端跨平台 `git` CLI，凭据委托宿主 OS，详见 [`RemoteOS.GitClient.md`](./applications/RemoteOS.GitClient.md)） |

---

## 7. 未来应用规划

### RemoteBrowser

- **定位**：不是远程浏览器。网页内容走客户端网络由平台原生引擎渲染（Win=WebView2/macOS=WKWebView/Linux=WebKitGTK）。
- **结构**：`RemoteBrowser → RemoteWindow → NativeWebView (Avalonia.Controls.WebView 12.0.1)`。
- **网页**：本地加载。
- **同步到 Server**：History、Bookmark（已实现，按用户隔离，EF Core+SQLite 持久化）；BrowserSettings（已实现，随 Workspace 持久化）；Cookie/Extension Config（未实现）。
- **端口转发**（已实现）：由 Port Forwarding 应用显式建立仅 `127.0.0.1` 监听的 SSH 本地转发；端口冲突时返回替代链接。SSH 设置与运行中隧道仅保存在 Client 本机。
- **已实现**：导航（后退/前进/刷新/停止/主页/地址栏）+ 书签（加入/删除/侧边栏双击导航/清空全部）+ 历史（自动记录访问/侧边栏双击导航/单条删除/清空全部）+ 浏览器偏好持久化；JWT via IAuthSession；未登录弹提示窗。详见 [`RemoteOS.Browser.md`](./applications/RemoteOS.Browser.md)。

### Port Forwarding

- **定位**：Client 本机 SSH 隧道管理器；可由浏览器或第一方服务请求，不经 Server 同步配置。
- **已实现**：优先绑定请求端口、冲突时自动选取可用 loopback 端口、返回实际链接、运行中隧道列表、更新与停止。详见 [`RemoteOS.PortForwarding.md`](./applications/RemoteOS.PortForwarding.md)。

### RemoteTaskManager

- **定位**：远端宿主 OS 任务管理器，参考 Windows 任务管理器 / GNOME 系统监视器。
- **结构**：`TaskManagerApp → RemoteWindow → TaskManagerMainView`（性能 / 进程两个标签页）。
- **数据源**：Server 端 `ISystemMetricsProvider` 以宿主 OS 进程身份实时采集（CPU/内存/磁盘/网络/GPU + 进程列表），**不持久化**（每次请求当下快照）。
- **跨平台抽象**：与 `IIdentityProvider` 同模式——`WindowsMetricsProvider`（GetSystemTimes + GlobalMemoryStatusEx）/ `LinuxMetricsProvider`（/proc/stat + /proc/meminfo + /proc/[pid]/status），平台差异封装在 Provider 之后。
- **已实现**：性能页（CPU 整机+每核+柱状图 / 内存柱状图 / 磁盘 / 网络速率 / GPU nvidia-smi / 运行时间，2s 自动刷新）+ 进程页（列表 + 按名称/PID/用户过滤 + 结束任务，权限不足提示需在宿主 OS 提权）；JWT via IAuthSession；未登录弹提示窗。详见 [`RemoteOS.TaskManager.md`](./applications/RemoteOS.TaskManager.md)。

### RemoteTerminal

支持两种模式：

- **Remote Mode**：运行于 Server。**已实现**——PTY 运行于 `RemoteOS.Server`，经 SignalR Hub（`/hubs/terminals`）流式传输到 Client。Server 端是 PTY 哑中继（只转发字节），VT 渲染在客户端 `TerminalControl` 完成。JWT 通过 SignalR `AccessTokenProvider` 鉴权。详见 [`RemoteOS.Terminal.md`](./applications/RemoteOS.Terminal.md)。
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
- **已实现**：UI 移植自 Jaya File Manager（BSD-3），导航树（懒加载）+ Explorer 网格 + 地址栏 + 工具栏 + 状态栏。所有文件操作经 Server 端 REST API（`/api/v1/files/*`）执行，Server 以宿主 OS 进程身份执行 `System.IO`，复用宿主用户/权限（不另建 ACL——project_memory 硬约束）。支持浏览、新建文件夹/删除/重命名/复制/移动/上传/下载、文件/目录属性查看（Linux 可编辑 POSIX 权限），以及依据应用 manifest 扩展名声明的默认打开与“打开方式”；危险操作（删除）弹确认对话框。JWT 复用 `IAuthSession`。详见 [`RemoteOS.Explorer.md`](./applications/RemoteOS.Explorer.md)。

---

## 8. 已完成里程碑

| 阶段 | 内容 | 状态 |
|------|------|------|
| 阶段 0 | Desktop / Wallpaper / Icon / Taskbar / WindowManager | 完成（+ 宿主窗口控制 / mstsc 连接栏 / 模态对话框，见 [`RemoteOS.Desktop.md`](./desktop/RemoteOS.Desktop.md)） |
| 阶段 1 | Runtime / App.SDK / Launch App / Create Window / Modal Dialog | 完成 |
| 阶段 2 | RemoteBrowser / RemoteTerminal / RemoteExplorer / RemoteTaskManager | 完成（RemoteTerminal Local+Remote Mode；RemoteExplorer 浏览、文件打开方式、属性与基本操作；RemoteBrowser 导航+书签+历史+浏览器偏好；RemoteTaskManager 性能页+进程页，跨平台指标采集） |
| 阶段 3 | RemoteServer：Account / Workspace / Sync / Storage / Remote State | 完成（登录模块；服务端 SQLite 持久化——User/Workspace(含 TerminalSettings/BrowserSettings/Preferences)/Device/Bookmark/HistoryEntry 落库，见 [`RemoteOS.Storage.md`](./platform/RemoteOS.Storage.md)；设置中心——偏好持久化到 Workspace + 多设备同步，见 [`RemoteOS.Settings.md`](./desktop/RemoteOS.Settings.md)） |
| 阶段 4 | DockerManager / ProcessGuardian | 部分完成（DockerManager：状态/资源只读、容器启停重启、Compose 校验/部署/停止；ProcessGuardian：独立 Agent、IPC、工作负载启停重启；健康/日志/服务适配设计中，见 [`RemoteOS.DockerManager.md`](./applications/RemoteOS.DockerManager.md) / [`RemoteOS.ProcessGuardian.md`](./applications/RemoteOS.ProcessGuardian.md)） |

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

- **登录与身份**（Windows LogonUser、Linux PAM + NSS 均已实现）— 已完成，见 [`RemoteOS.Login.md`](./platform/RemoteOS.Login.md)；设计原则见 [`RemoteOS.Authentication.md`](./platform/RemoteOS.Authentication.md)
- **安全设计**（sudo / 权限 / 危险操作确认）— 见 [`RemoteOS.Security.md`](./platform/RemoteOS.Security.md)
- **Workspace / Session / Device 多设备模型** — 见 [`RemoteOS.Workspace.md`](./architecture/RemoteOS.Workspace.md)
- **云同步、Storage、Remote Runtime、Compute** — 见 [`RemoteOS.Architecture.md`](./architecture/RemoteOS.Architecture.md)
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

### 架构与核心模型

| 文档 | 用途 |
|------|------|
| [`Architecture`](./architecture/RemoteOS.Architecture.md) | 模块设计、依赖关系、架构原则 |
| [`Protocol`](./architecture/RemoteOS.Protocol.md) | 通信协议契约层、REST/SignalR、序列化约定 |
| [`Workspace`](./architecture/RemoteOS.Workspace.md) | User / Workspace / Session / Device、多设备、云桌面状态 |
| [`ApplicationActivation`](./architecture/RemoteOS.ApplicationActivation.md) | 应用启动 URI、窗口实例策略、受控 `remoteos://` 路由 |

### 平台服务

| 文档 | 用途 |
|------|------|
| [`Authentication`](./platform/RemoteOS.Authentication.md) | 登录系统、Linux 用户集成、身份模型 |
| [`Login`](./platform/RemoteOS.Login.md) | 登录窗口、auth 端点、JWT 与错误处理 |
| [`Security`](./platform/RemoteOS.Security.md) | 安全设计、权限提升与危险操作确认 |
| [`Storage`](./platform/RemoteOS.Storage.md) | EF Core + SQLite、持久化范围与表结构 |

### 桌面体验

| 文档 | 用途 |
|------|------|
| [`Desktop`](./desktop/RemoteOS.Desktop.md) | 桌面外壳、宿主窗口控制、模态对话框与键盘路由 |
| [`Settings`](./desktop/RemoteOS.Settings.md) | 设置中心、偏好持久化与多设备同步 |
| [`Localization`](./desktop/RemoteOS.Localization.md) | 多语言机制、语言包结构与 i18n 约束 |

### 内置应用

| 文档 | 用途 |
|------|------|
| [`Browser`](./applications/RemoteOS.Browser.md) | 浏览器、书签与历史 |
| [`CodeEditor`](./applications/RemoteOS.CodeEditor.md) | 代码编辑器与文件安全边界 |
| [`CertificateManager`](./applications/RemoteOS.CertificateManager.md) | ACME 证书生命周期、Kestrel 部署与续期 |
| [`DockerManager`](./applications/RemoteOS.DockerManager.md) | Docker Engine、容器与 Stack 管理 |
| [`Explorer`](./applications/RemoteOS.Explorer.md) | 文件管理器、REST API 与权限复用 |
| [`Firewall`](./applications/RemoteOS.Firewall.md) | Linux Server UFW 防火墙 |
| [`GitClient`](./applications/RemoteOS.GitClient.md) | Git 仓库版本控制、分支与提交 |
| [`NetworkInspector`](./applications/RemoteOS.NetworkInspector.md) | 网络诊断与分析 |
| [`PortForwarding`](./applications/RemoteOS.PortForwarding.md) | 本机 SSH loopback 隧道 |
| [`ProcessGuardian`](./applications/RemoteOS.ProcessGuardian.md) | 守护工作负载、健康检查与服务管理 |
| [`Registry`](./applications/RemoteOS.RegistryApp.md) | 配置注册表浏览与隔离边界（第一阶段只读） |
| [`TaskManager`](./applications/RemoteOS.TaskManager.md) | 系统指标、进程查看与管理 |
| [`Terminal`](./applications/RemoteOS.Terminal.md) | PTY、SignalR 与终端会话管理 |
| [`WebServerManager`](./applications/RemoteOS.WebServerManager.Design.md) | Web Server Provider、Nginx 集成与站点管理（设计中） |

### 开发与扩展

| 文档 | 用途 |
|------|------|
| [`AppSettings`](./development/RemoteOS.AppSettings.md) | 应用私有配置存储 |
| [`ApplicationCompatibility`](./development/RemoteOS.ApplicationCompatibility.md) | 应用兼容性、平台适配与降级策略 |
| [`BuiltInApplication.Conventions`](./development/RemoteOS.BuiltInApplication.Conventions.md) | 内置应用设计、国际化与跨平台约束 |
| [`Develop`](./development/RemoteOS.Develop.md) | 开发者快速上手、代码结构与调试指南 |
| [`DeveloperMode`](./development/RemoteOS.DeveloperMode.md) | 开发模式、DevCli 与应用包发布 |
