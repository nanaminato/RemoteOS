# RemoteOS 项目说明文档

> 本文档描述 RemoteOS 当前实现状态：Solution 结构、项目列表、代码地图、当前 MVP 进度、开发状态。
>
> - 架构设计原则见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)
> - 用户 Workspace 模型见 [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)
> - 登录与身份模型见 [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md)
> - 安全设计见 [`RemoteOS.Security.md`](./RemoteOS.Security.md)
> - 桌面外壳与模态对话框见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)
>
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

本地 RemoteOS Shell 已完成（Desktop、Window Manager、Application Runtime、Application SDK、内置应用 Welcome/Notepad/Settings）。

桌面外壳已增强：宿主窗口控制（标题栏拖动 / 8 向 resize / 最小化·最大化·关闭 / 全屏）、mstsc 风格连接栏（全屏切换、固定与自动隐藏、连接信息、关闭连接 = 登出）、可复用模态对话框机制（`AppContext.ShowDialogAsync`，支持嵌套与任意结果类型）。详见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)。

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
    RemoteOS.Protocol            通信协议契约（占位）
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
- **包含内置应用**：Welcome、Notepad、Settings。
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

- **定位**：通信协议契约。当前为占位。
- **包含**：DTO、Message、API Contract、Client Proxy 契约（契约位于 Protocol，代理实现位于 Client）。详见 [`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md)。
- **规则**：所有 Client / Server 通信必须经过 Protocol。禁止业务代码直接调用 HTTP / WebSocket。

### 4.9 RemoteOS.Server

- **定位**：RemoteOS Cloud Backend，**跨平台运行于 Ubuntu / Windows Server**。已实现 auth 端点（login/refresh/logout/me）+ JWT + `IIdentityProvider`（`WindowsLogonProvider` 迁移自测试床，`LinuxPamProvider` 占位）+ 内存仓储（User/Workspace/Session/Device）。详见 [`RemoteOS.Login.md`](./RemoteOS.Login.md)。
- **负责**：Authentication、Identity Mapping（跨平台 OS 用户集成）、Workspace、Session、Device、Storage、Sync、Remote Runtime、Compute、Security Integration。
- **架构**：单一代码库 + OS 抽象层（`IIdentityProvider` 等接口 + Linux/Windows 各自实现），平台差异封装在抽象之后。
- **不负责**：UI Rendering、Window Management、Screen Streaming。
- **详见**：[`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md)、[`RemoteOS.Security.md`](./RemoteOS.Security.md)、[`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)

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

- **MVP 阶段**：Manifest 由代码创建。
- **未来**：支持应用包加载。

---

## 6. 内置应用规划

| 应用 | 用途 | 状态 |
|------|------|------|
| **Welcome** | 验证 Runtime、WindowManager | 已实现 |
| **Notepad** | 验证 Application Lifecycle、Window Interaction | 已实现 |
| **Settings** | 系统设置入口 | 已实现 |

---

## 7. 未来应用规划

### RemoteBrowser

- **定位**：不是远程浏览器。
- **结构**：`RemoteBrowser → Avalonia Window → WebView2 → Chromium`。
- **网页**：本地加载。
- **同步到 Server**：History、Bookmark、Cookie、Extension Config。

### RemoteTerminal

支持两种模式：

- **Local Mode**：运行于 Client，例如 PowerShell / CMD / Bash。
- **Remote Mode**：运行于 Server。

  ```text
  Terminal UI
      |
  SignalR (over WebSocket)
      |
  RemoteServer
      |
  PTY
      |
  Shell
  ```

  > PTY 高吞吐流若 SignalR 性能不足，未来可切换裸 WebSocket 端点（如 `/terminals/{id}/stream`）；此决策在 RemoteTerminal 实现阶段再定。调研 RoyalTerminal（传输无关的终端 UI 栈）结论：SignalR 与裸 WebSocket 均可行。

### RemoteExplorer

- **定位**：远程文件管理，**不是**远程桌面文件浏览。
- **结构**：`Explorer UI → RemoteServer API → Remote File System`。

---

## 8. MVP 开发计划

| 阶段 | 内容 | 状态 |
|------|------|------|
| MVP 0 | Desktop / Wallpaper / Icon / Taskbar / WindowManager | 完成（+ 宿主窗口控制 / mstsc 连接栏 / 模态对话框，见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)） |
| MVP 1 | Runtime / App.SDK / Launch App / Create Window / Modal Dialog | 完成 |
| MVP 2 | RemoteBrowser / RemoteTerminal / RemoteExplorer | 进行中（雏形：Welcome/Notepad/Settings） |
| MVP 3 | RemoteServer：Account / Workspace / Sync / Storage / Remote State | 进行中（登录模块 MVP 已完成） |

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

- **登录与身份**（Windows LogonUser 已实现，Linux PAM 占位）— MVP 已完成，见 [`RemoteOS.Login.md`](./RemoteOS.Login.md)；设计原则见 [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md)
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
| [`RemoteOS.Security.md`](./RemoteOS.Security.md) | 安全设计、sudo、权限提升、危险操作确认 |
| [`RemoteOS.md`](./RemoteOS.md) | 项目结构、代码位置、当前进度 |
