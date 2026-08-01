RemoteOS 架构设计文档
===

> 本文档定义 RemoteOS 的**架构设计**：各模块的定位、职责边界、依赖约束与架构原则。
> 文件清单与代码地图见 [`RemoteOS.md`](./RemoteOS.md) 第 12–13 节；本文档不重复。
> 当两者冲突时，实现以 `RemoteOS.md` 为准，原则以本文档为准。

---

1. 解决方案结构
---

`RemoteOS.sln` 包含 9 个项目，按职责分层（详见 `RemoteOS.md` §12）：

```
Client/      RemoteOS.Client            桌面 Shell + 内置应用（类库）
             RemoteOS.Client.Desktop    平台入口（WinExe，Program.cs）
Framework/   RemoteOS.Core              平台无关原语与类型
             RemoteOS.UI                Avalonia 共享主题/样式
             RemoteOS.WindowManager     窗口管理器 + RemoteWindow 控件
             RemoteOS.App.SDK           应用开发面（AppContext / IRemoteApplication）
             RemoteOS.Runtime           应用运行时（ApplicationManager）
Shared/      RemoteOS.Protocol          通信协议契约（占位）
             RemoteOS.Server            服务端（ASP.NET Core，占位）
```

依赖方向（自下而上，禁止反向依赖）：

```
Core ─► UI ─► WindowManager ─► App.SDK ─► Runtime ─► Client ─► Client.Desktop
                                                          ▲
Protocol ──────────────────────────────────────────────┬──┘
                                                        └──► Server
```

2. 模块定位与边界
---

### 2.1 RemoteOS.Client.Desktop（平台入口）

定位：RemoteOS 的可执行入口。类似操作系统的引导加载器。

职责：
- 配置 Avalonia（`AppBuilder`：平台检测、字体、日志）
- 启动经典桌面生命周期
- 委托给 `RemoteOS.Client` 的 `App`

不应该包含：任何业务逻辑、窗口逻辑、应用逻辑。

### 2.2 RemoteOS.Client（Shell + 装配层）

定位：桌面 Shell，整个系统的入口体验。类似 Windows `explorer.exe`。

职责：
- 启动 Shell（`App.axaml` / `Bootstrapper` DI 装配）
- 创建主窗口（`MainWindow`：全屏无边框）
- 提供 Desktop / Taskbar / StartMenu / WindowHost 视图
- 内置应用（Welcome / Notepad / Settings）
- 初始化系统服务（WindowManager、ApplicationManager、ShellSettings）

不应该包含：窗口管理算法、应用运行时机制、网络逻辑。这些下沉到 Framework 层。

### 2.3 RemoteOS.Core（系统基础抽象层）

定位：平台无关的基础类型层。所有模块依赖 Core。

包含：
- 几何原语（`Point` / `Size` / `Rect`）
- 窗口模型（`WindowInfo` / `WindowId` / `WindowState` / `WindowChangedEventArgs`）
- 应用模型（`AppId` / `ApplicationManifest` / `ApplicationInfo`）

**严格禁止**：Core 不引用 Avalonia、网络、数据库。Core 必须保持纯净——这是整个架构可移植的根基。

### 2.4 RemoteOS.UI（统一视觉层）

定位：系统统一 UI 组件与主题。类似 WinUI / Material Design。

职责：提供 Windows 11 风格暗色视觉语言（Accent 色板、Button / TextBox / ListBoxItem / Border / ScrollViewer 样式，`RemoteWindow` 控件模板）。

所有系统 UI 应复用这里的样式，保证视觉一致性。

### 2.5 RemoteOS.WindowManager（核心：窗口管理）

定位：RemoteOS 最核心模块。负责模拟操作系统窗口管理。

架构：

```
WindowManager（权威状态机：生命周期 / Z-order / 焦点 / 状态）
        │
RemoteWindow（TemplatedControl：自处理拖动 / resize / 双击 / 聚焦交互）
        │
Avalonia Control（渲染）
```

功能：Create / Close / Move / Resize（8 向）/ Focus / Minimize / Maximize / Z-Index / 任务栏切换。

启动应用的窗口创建链路：

```
User Click Icon → ApplicationManager.Launch → AppContext.ShowWindow
→ WindowManager.Create → RemoteWindow 渲染
```

不应该包含：应用业务逻辑、应用注册表（那是 Runtime 的职责）。

### 2.6 RemoteOS.App.SDK（应用开发面）

定位：第三方应用开发接口。类似 Windows SDK / Android SDK / Electron Runtime。

提供：
- ✅ Window API：`AppContext.ShowWindow()` / `WindowManager.Create()`
- ⏳ Storage API：`Storage.Save()` / `Storage.Load()`（待 RemoteServer）
- ⏳ Sync API：`Sync.Push()` / `Sync.Pull()`（待 RemoteServer）
- ⏳ Remote API：`RemoteClient.Execute()`（待 RemoteServer）

应用通过实现 `IRemoteApplication`（或继承 `RemoteApplicationBase`）+ `Activate(AppContext)` 接入系统。

### 2.7 RemoteOS.Runtime（应用运行时）

定位：应用运行环境。RemoteOS 应用不是 exe。

启动流程：

```
Desktop Icon → ApplicationManager.Launch(AppId)
→ 构造 AppContext → IRemoteApplication.Activate(context)
→ AppContext.ShowWindow() → WindowManager.Create()
```

职责：应用注册、加载、生命周期管理。不应该包含：窗口管理算法（委托 WindowManager）、UI 控件。

### 2.8 RemoteOS.Protocol（通信协议）

定位：Client 与 Server 通信协议契约。

**严格禁止**：业务代码直接调用 HTTP / WebSocket。所有通信必须经过 `RemoteOS.Protocol`。

包含（规划）：DTO / Message / API Client / WebSocket Client。当前为占位，属 MVP 3。

### 2.9 RemoteOS.Server（云后端）

定位：Cloud Backend。提供 Data / State / Storage / Compute API。

**严格禁止**：UI Rendering / Window Management / Screen Streaming。Server 永远不碰渲染。

当前为 ASP.NET Core 默认模板占位，属 MVP 3。

3. RemoteApplication 规范
---

RemoteOS 中的应用不是普通程序，而是受 Runtime 管理的本地组件。

结构（规划）：

```
Application Package
├── Manifest.json
├── UI
├── Logic
├── State Manager
└── Remote Connector
```

MVP 阶段：Manifest 由应用代码内构造（`ApplicationManifest` record），`Activate` 直接创建窗口。
后续：支持从磁盘包加载。

4. 内置应用规范（规划）
---

MVP 2 目标应用（当前仅 Welcome / Notepad / Settings 验证链路）：

- **RemoteBrowser**：不是远程浏览器。`RemoteBrowser → Avalonia Window → WebView2 → Chromium`。网页本地加载、本地渲染；History / Bookmark / Cookie / Extension Config 通过 Sync Service 同步。
- **RemoteTerminal**：本地模式（PowerShell / CMD / Bash）+ 远程模式（WebSocket → RemoteServer → SSH Shell）。
- **RemoteExplorer**：远程文件管理（非远程桌面）。`Explorer → RemoteServer API → Remote File System`。

5. MVP 开发顺序
---

| 阶段 | 内容 | 状态 |
|------|------|------|
| **MVP 0** | Desktop / Wallpaper / Icon / Taskbar / WindowManager | ✅ 完成 |
| **MVP 1** | Runtime / App.SDK / Launch App / Create Window | ✅ 完成 |
| **MVP 2** | RemoteBrowser / RemoteTerminal / RemoteExplorer | 🔶 雏形（Welcome/Notepad/Settings） |
| **MVP 3** | RemoteServer：Account / Sync / Storage / Remote State | ⏸ 暂缓 |

6. AI Agent 必须遵守规则
---

### 严格禁止

不要实现：Screen Capture / Desktop Streaming / RDP / VNC / Framebuffer / Image Transfer。

RemoteOS 不是：Remote Desktop。

### 正确实现方式

- 应用：Local Execution + Local Rendering
- RemoteServer：仅提供 Data / State / Storage / Compute API

### 架构纪律

- Core 保持纯净，不引用 Avalonia / 网络 / 数据库
- 业务代码不直接调用 HTTP / WebSocket，必须经过 Protocol
- 窗口逻辑只在 WindowManager，应用逻辑只在 Runtime，Shell 只做装配
- 依赖只能自下而上，禁止反向

7. 当前开发重点
---

优先级（自上而下）：

1. RemoteOS.Client（Shell 体验）
2. RemoteOS.WindowManager（窗口管理）
3. RemoteOS.Core（基础抽象）
4. RemoteOS.Runtime（应用运行时）
5. RemoteOS.App.SDK（应用开发面）

不要提前开发：用户系统 / 云同步 / 权限系统 / 文件服务器 / Docker 管理。

先完成：一个可以运行应用、管理窗口的本地桌面操作系统。

---

### AI Agent 任务理解总结

修改 RemoteOS 代码时必须认为：

- RemoteOS = Operating System Shell
- RemoteServer = Cloud Backend
- Application = Local Runtime Component

任何设计必须优先考虑：

- 本地 UI 渲染
- 模块化 Application Runtime
- Window Manager 管理窗口
- RemoteServer 提供云能力

不要把 RemoteOS 演变成：Remote Desktop Tool / Server Management Panel / Web Dashboard。
