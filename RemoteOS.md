RemoteOS 项目开发规范文档
===

> 本文档分为两部分：**愿景规范**（第 1–11、14 节，描述 RemoteOS 的设计目标与约束）与
> **当前实现**（第 12–13 节，描述 MVP 阶段已落地的项目结构与代码地图）。
> 当代码与愿景存在差异时，以第 12–13 节为准。

---

1. 项目定位
---

RemoteOS 是一个跨平台云原生桌面操作系统环境。

目标是在 Windows/Linux/macOS 上为 Ubuntu 提供类似 Windows Desktop 的用户体验。

RemoteOS 不是远程桌面软件。

禁止将 RemoteOS 理解为：

- RDP
- VNC
- AnyDesk
- TeamViewer
- 云桌面串流系统

RemoteOS 的核心理念：

应用程序运行在本地设备，UI 在本地渲染；RemoteServer 负责提供数据、状态同步、存储和远程计算能力。

类似：

```
Windows:
  Windows Kernel → Window Manager → Application → Local Rendering

RemoteOS:
  RemoteOS Runtime → Window Manager → RemoteApplication → Local Rendering
                                    +
  RemoteServer (Data / State / Storage / Compute)
```

2. 技术架构
---

### 2.1 RemoteOS Client

技术：

- .NET 10
- Avalonia UI
- CommunityToolkit.Mvvm（MVVM 框架，源生成器驱动 ObservableObject / RelayCommand）
- Microsoft.Extensions.DependencyInjection（依赖注入）
- WebView2（规划中，用于 RemoteBrowser）

> 说明：原规划提及 ReactiveUI，当前 MVP 实现统一采用 CommunityToolkit.Mvvm。
> 后续若引入响应式数据流可再评估 ReactiveUI 的融合方式。

目标：实现一个跨平台桌面环境。

运行平台：Windows / Linux / macOS

核心组件：

```
RemoteOS.Client
├── Shell
├── Desktop
├── Window Manager
├── Taskbar
├── Application Runtime
├── Resource Manager（规划）
├── Settings
└── Remote Protocol Client（规划）
```

### 2.2 RemoteServer

技术：

- .NET 10
- ASP.NET Core
- Entity Framework Core（规划）
- PostgreSQL / LiteDB（规划）
- WebSocket
- REST API

负责：

- User Management
- Application Data
- File Storage
- Synchronization
- Remote Task Execution
- Compute Service

不负责：

- UI Rendering
- Window Management
- Screen Streaming

> 当前状态：`RemoteOS.Server` 仍为 ASP.NET Core 默认模板（OpenAPI + WeatherForecast 占位），
> 尚未实现任何业务服务。MVP 阶段按需求暂不涉及服务端。

3. 总体架构
---

```
+------------------------------------------------+
|                RemoteOS Client                 |
|                                                |
|  +--------------------------------------------+|
|  |              RemoteOS Shell                ||
|  |  Desktop / Window Manager / Taskbar / ...  ||
|  +--------------------------------------------+|
|  |          Application Runtime               ||
|  |  RemoteBrowser / RemoteTerminal / ...      ||
|  +--------------------------------------------+|
|              Avalonia Rendering                |
+------------------------------------------------+
                     |
              RemoteOS Protocol
                     |
+------------------------------------------------+
|                 RemoteServer                   |
|  User / Storage / Sync / Application / Compute |
+------------------------------------------------+
```

4. RemoteOS Client 设计规范
---

### 4.1 Shell

RemoteOS Shell 是整个系统入口。负责 Desktop、Taskbar、Window Manager、Application Launcher。
类似 Windows `explorer.exe`。

启动链路（当前实现）：

```
RemoteOS.Client.Desktop (Program.cs / 入口)
        |
        v
  Avalonia App (App.axaml)
        |
        v
  Bootstrapper (DI 装配)
        |
        v
  DesktopShellViewModel + DesktopShellView
        |
        v
  Desktop / Taskbar / StartMenu / WindowHost
```

5. Window Manager
---

RemoteOS 的核心。所有应用必须运行在 Window 中。

Window Manager 负责：

- 生命周期：Create / Open / Close / Suspend / Restore / Destroy
- 窗口行为：Move / Resize / Minimize / Maximize / Focus / Z-Index / Dock（规划）

> 当前实现见第 13.3 节：`RemoteOS.WindowManager` 提供 `RemoteWindow` 控件 + `WindowManager`
> 权威状态机，支持拖动、8 向 resize、最小化/最大化/还原、焦点、Z-order、任务栏切换、
> 双击标题栏最大化。

6. Application Runtime
---

RemoteOS 应用不是传统 exe。

应用模型：`RemoteApplication`（通过 `IRemoteApplication` / `RemoteApplicationBase` 实现）。

应用结构（规划）：

```
Application Package
├── Manifest.json
├── UI
├── Logic
├── Remote Connector
└── State Manager
```

> 当前实现见第 13.4–13.5 节：`RemoteOS.Runtime.ApplicationManager` 负责注册与启动应用，
> `RemoteOS.App.SDK.AppContext` 为应用提供 `ShowWindow()` 等窗口 API。MVP 阶段 Manifest
> 由应用代码内构造，尚未支持从磁盘包加载。

7. RemoteOS App SDK
---

提供给应用开发者。类似 Windows SDK / Android SDK / Electron Runtime。

提供（规划）：

- Window API：`Window.Create()` / `Window.Show()` / `Window.Close()`
- Storage API：`RemoteStorage.Save()` / `RemoteStorage.Load()`
- Sync API：`SyncService.Push()` / `SyncService.Pull()`
- Remote API：`RemoteClient.Execute()`

> 当前实现仅包含 Window API（`AppContext.ShowWindow()`）。Storage / Sync / Remote API 待
> RemoteServer 接入后实现。

8. 内置应用规范
---

### 8.1 RemoteBrowser（规划）

RemoteOS 浏览器不是 Chrome 镜像。实现：`RemoteBrowser App → Avalonia Window → WebView2 → Chromium Engine`。
网页本地渲染；History / Bookmark / Cookie / Extension Config / Browser Setting 通过 Sync Service 同步到 RemoteServer。

### 8.2 RemoteFileManager（规划）

Remote File Explorer。显示远程 Linux 文件系统（home / projects / docker …）。
数据来源：RemoteServer File API → Linux Filesystem。双击下载元数据并打开本地应用。

### 8.3 RemoteTerminal（规划）

支持两种模式：Local Terminal（PowerShell / CMD / Bash）与 Remote Terminal（WebSocket → RemoteServer → SSH Shell）。

> 当前 MVP 内置应用为 **Welcome / Notepad / Settings**（见第 13.6 节），用于验证 Shell +
> Window Manager + App Runtime 链路。RemoteBrowser / RemoteFileManager / RemoteTerminal 为
> 后续 MVP 2 目标。

9. RemoteServer API 设计
---

通信方式：HTTPS REST API + WebSocket。

主要服务：User Service / Storage Service / Sync Service / Compute Service。

> 当前未实现，属 MVP 3 阶段。

10. MVP 开发路线
---

### MVP 0 — RemoteOS Shell ✅ 已完成

目标：启动一个虚拟桌面。

功能：

- ✅ Desktop
- ✅ Wallpaper（5 种渐变预设，可在 Settings 中实时切换）
- ✅ Icon（桌面图标 + 开始菜单入口）
- ✅ Taskbar（窗口列表 + 激活指示条 + 时钟）
- ✅ Window Manager（拖动 / 8 向 resize / 最小化 / 最大化 / 焦点 / Z-order）

### MVP 1 — Application Runtime ✅ 已完成

实现：`RemoteOS.App.SDK`

支持：

- ✅ Launch App（桌面图标 / 开始菜单点击启动）
- ✅ Create Window（`AppContext.ShowWindow()`）
- ⏳ 应用包从磁盘加载（当前 Manifest 在代码内构造）

### MVP 2 — Built-in Applications 🔶 雏形已完成

- ✅ Welcome / Notepad / Settings（验证链路的内置应用）
- ⏳ RemoteBrowser（Avalonia + WebView2）
- ⏳ RemoteTerminal（Local / Remote Shell）
- ⏳ RemoteExplorer（远程文件浏览 + 同步）

### MVP 3 — RemoteServer ⏸ 按需求暂缓

- ⏳ Account / Sync / Storage / Application State

11. AI Agent 开发约束
---

**IMPORTANT: RemoteOS is NOT Remote Desktop.**

禁止实现：

- Screen Capture
- Desktop Streaming
- RDP
- VNC
- Remote Framebuffer
- Image Transfer

应用必须：

- Run locally
- Render locally

RemoteServer 只能提供：Data / State / Storage / Compute API。

12. 项目结构（当前实现）
---

解决方案 `RemoteOS.sln` 包含 **9 个项目**，按职责分层组织在四个顶层目录下：

```
RemoteOS/
├── Client/                          # 客户端：Shell、应用、入口
│   ├── RemoteOS.Client/             #   桌面 Shell + 内置应用（类库）
│   └── RemoteOS.Client.Desktop/     #   平台入口（WinExe，Program.cs）
├── Framework/                       # 框架：可复用的分层基础库
│   ├── RemoteOS.Core/               #   平台无关原语与类型
│   ├── RemoteOS.UI/                 #   Avalonia 共享主题/样式
│   ├── RemoteOS.WindowManager/      #   窗口管理器 + RemoteWindow 控件
│   ├── RemoteOS.App.SDK/            #   应用开发面（AppContext / IRemoteApplication）
│   └── RemoteOS.Runtime/            #   应用运行时（ApplicationManager）
├── Shared/                          # 客户端与服务端共享
│   └── RemoteOS.Protocol/           #   通信协议契约（占位）
└── RemoteOS.Server/                 # 服务端（ASP.NET Core，占位）
```

### 12.1 依赖关系图

```
                    RemoteOS.Core (无依赖)
                  ─────────┬─────────────
              ┌────────────┼────────────┐
              v            v            v
        RemoteOS.UI   WindowManager   (被多处引用)
              │            │
              └─────┬──────┘
                    v
               App.SDK ──► WindowManager, Core
                    │
                    v
               Runtime ──► App.SDK, Core
                    │
   ┌────────────────┴────────────────┐
   v                                 v
 RemoteOS.Client ──► Core, Protocol, App.SDK, Runtime, UI, WindowManager
   │
   v
 RemoteOS.Client.Desktop ──► Client (+ Avalonia.Desktop)

 RemoteOS.Server ──► Protocol          (独立于客户端链路)
```

层级约束：

- **Core** 不依赖任何 UI 框架，保持平台无关。
- **UI / WindowManager** 依赖 Core + Avalonia。
- **App.SDK** 依赖 Core + WindowManager（应用通过 SDK 创建窗口）。
- **Runtime** 依赖 App.SDK + Core（不直接依赖 WindowManager，通过 SDK 间接访问）。
- **Client** 是装配层，依赖上述所有框架项目。
- **Client.Desktop** 仅依赖 Client，提供平台启动入口。
- **Protocol / Server** 独立于客户端链路，供 MVP 3 使用。

### 12.2 包管理

采用中央包管理（`Directory.Packages.props`，位于仓库根）。所有 `<PackageReference>` 不指定版本，
统一由 props 文件管控。关键包：

- Avalonia 12.1.0（Avalonia / Themes.Fluent / Fonts.Inter / Desktop）
- AvaloniaUI.DiagnosticsSupport 2.2.3（仅 Debug 启用）
- CommunityToolkit.Mvvm 8.4.2
- Microsoft.Extensions.DependencyInjection 10.0.0
- Microsoft.AspNetCore.OpenApi 10.0.10（Server）

13. 代码地图（当前实现）
---

### 13.1 RemoteOS.Core（平台无关原语）

| 文件 | 职责 |
|------|------|
| `Primitives/Point.cs` `Size.cs` `Rect.cs` | 桌面坐标系下的不可变几何原语（与 Avalonia 类型解耦） |
| `Applications/AppId.cs` | 应用强类型标识 `readonly record struct AppId(string)` |
| `Applications/ApplicationManifest.cs` | 应用清单（Id / DisplayName / Version / IconGlyph / Description） |
| `Applications/ApplicationInfo.cs` | 对外公开的应用元数据（Manifest → Info 转换） |
| `Windows/WindowId.cs` | 窗口强类型标识 `readonly record struct WindowId(int)` |
| `Windows/WindowState.cs` | 窗口状态枚举：Normal / Minimized / Maximized |
| `Windows/WindowInfo.cs` | 平台无关的窗口描述（Bounds / RestoreBounds / MinSize / State / 能力位） |
| `Windows/WindowChangedEventArgs.cs` | 窗口变更事件参数（StateChanged / BoundsChanged / TitleChanged / FocusChanged） |

### 13.2 RemoteOS.UI（共享主题）

| 文件 | 职责 |
|------|------|
| `Themes/Styles.axaml` | Windows 11 风格暗色视觉语言：Accent 色板、Button（默认/primary/icon/caption/close）、TextBox、ListBoxItem、Border（surface/card）、ScrollViewer 等 |

### 13.3 RemoteOS.WindowManager（窗口管理器）

| 文件 | 职责 |
|------|------|
| `IWindowManager.cs` | 窗口管理器接口：Windows / ActiveWindow / HostBounds、Attach / SetHostBounds / Create / Close / Focus / Minimize / Restore / ToggleMaximize + 事件 |
| `WindowManager.cs` | 权威实现：窗口生命周期、Z-order（自增 ZIndex）、焦点、最小化/最大化/还原状态机、拖动 Clamp、resize 边界计算、级联初始位置 |
| `RemoteWindow.cs` | `TemplatedControl`：自处理标题栏拖动、8 向 resize、双击最大化、点击聚焦；通过事件将交互委托给 WindowManager |
| `ManagedWindow.cs` | 窗口的 VM / 公共句柄（绑定到 chrome 与任务栏）：Title / IconGlyph / State / IsActive / 能力位 + Focus/Close/Minimize/ToggleMaximize/TaskbarToggle 命令 |
| `WindowCreateOptions.cs` | 创建窗口的参数 record（OwnerAppId / Title / Content / Bounds / IconGlyph / CanResize / CanMinimize / CanMaximize） |
| `ResizeEdge.cs` | `[Flags]` 枚举：8 向 resize 边缘（Left/Top/Right/Bottom 及组合） |
| `WindowInteractionEventArgs.cs` | `DragBoundsEventArgs` / `ResizeBoundsEventArgs`：携带按下时的 bounds 与当前 delta |
| `Themes/RemoteWindowTheme.axaml` | RemoteWindow 控件模板：标题栏（图标 + 标题 + caption 按钮）、内容宿主、8 向 resize grip 层、active/inactive 视觉差异 |

### 13.4 RemoteOS.App.SDK（应用开发面）

| 文件 | 职责 |
|------|------|
| `IRemoteApplication.cs` | 应用接口：`Manifest` + `Activate(AppContext)` |
| `RemoteApplicationBase.cs` | 便捷基类，默认空 `Activate` |
| `AppContext.cs` | 启动上下文：AppId / WindowManager / Services；`ShowWindow()` 便捷创建窗口 |

### 13.5 RemoteOS.Runtime（应用运行时）

| 文件 | 职责 |
|------|------|
| `ApplicationManager.cs` | 应用注册表 + 启动器：`Register` / `IsRegistered` / `Get` / `Launch(AppId)`（构造 AppContext 并调用 `Activate`）；`Registered` 提供桌面/开始菜单元数据 |

### 13.6 RemoteOS.Client（Shell + 内置应用 + 装配）

```
RemoteOS.Client/
├── App.axaml(.cs)                  # Avalonia Application：加载主题、DI 装配、创建 MainWindow
├── Services/
│   ├── Bootstrapper.cs             # DI 容器装配：WindowManager / ApplicationManager /
│   │                               #   ShellSettings / 内置应用 / DesktopShellViewModel，并注册应用、填充桌面
│   ├── ShellSettings.cs            # 外观状态（壁纸索引），5 种渐变壁纸，CurrentWallpaper
│   └── WallpaperOption.cs          # 壁纸预设 record(Name, Brush)
├── ViewModels/Shell/
│   ├── DesktopShellViewModel.cs    # Shell 根 VM：WindowManager、Windows 列表、桌面/开始菜单图标、
│   │                               #   时钟、ToggleStart / Launch / Shutdown / ToggleTaskbarItem 命令
│   └── AppEntryViewModel.cs        # 桌面/开始菜单条目 VM：DisplayName / IconGlyph / Launch 命令
├── Views/
│   ├── MainWindow.axaml(.cs)       # 全屏无边框窗口（Maximized + WindowDecorations=None），承载 DesktopShellView
│   └── Shell/DesktopShellView.axaml(.cs)
│                                   # 桌面 Shell：壁纸、桌面图标 ItemsControl、PART_WindowHost Canvas、
│                                   # 开始按钮、任务栏窗口列表、时钟、开始菜单浮层；Loaded 时 Attach 窗口管理器
└── Apps/                           # 内置应用（每个 = App + View.axaml(.cs) + ViewModel）
    ├── WelcomeApp.cs  / WelcomeView(.cs) / WelcomeViewModel.cs
    ├── NotepadApp.cs  / NotepadView(.cs) / NotepadViewModel.cs
    └── SettingsApp.cs / SettingsView(.cs) / SettingsViewModel.cs
```

内置应用说明：

- **Welcome**（`remoteos.welcome`）：首启介绍窗口；按钮可启动 Notepad。
- **Notepad**（`remoteos.notepad`）：极简文本编辑器，New 按钮清空，状态栏显示字符数。
- **Settings**（`remoteos.settings`）：壁纸选择（ListBox 绑定 ShellSettings.Wallpapers），实时切换桌面背景；含 About 卡片。

### 13.7 RemoteOS.Client.Desktop（平台入口）

| 文件 | 职责 |
|------|------|
| `Program.cs` | `[STAThread]` 入口：`AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()` + 经典桌面生命周期 |
| `app.manifest` | Windows 兼容性清单（Win10 supportedOS） |
| `RemoteOS.Client.Desktop.csproj` | `WinExe`，引用 Avalonia.Desktop + RemoteOS.Client |

### 13.8 RemoteOS.Protocol / RemoteOS.Server（占位）

- `RemoteOS.Protocol`：空类库，作为客户端/服务端共享契约的落点（MVP 3 启用时填充）。
- `RemoteOS.Server`：ASP.NET Core 默认模板（OpenAPI + WeatherForecast 占位），引用 Protocol。尚未实现业务服务。

14. 运行方式
---

```bash
# 构建
dotnet build RemoteOS.sln

# 运行桌面（MVP 0 + 1 + 2 雏形）
dotnet run --project Client/RemoteOS.Client.Desktop
```

桌面启动后：

- 点击**桌面图标**或**开始菜单**启动应用（Welcome / Notepad / Settings）。
- 拖动**标题栏**移动窗口；拖动**窗口边缘** 8 向 resize。
- **双击标题栏**或点击 caption 按钮最大化/还原/最小化/关闭。
- 点击**任务栏图标**：最小化的还原、活动的最小化、其余的聚焦置顶。
- 在 **Settings** 中实时切换壁纸。

15. 最终定位
---

一句话：RemoteOS 是一个跨平台云原生桌面操作系统环境，它提供类似 Windows 的桌面体验，由 RemoteOS Runtime 管理应用，应用界面本地渲染，而用户数据、状态和计算能力可以同步到 RemoteServer。

核心研发方向：

- RemoteOS Runtime
- RemoteOS Window Manager
- RemoteOS App SDK
- RemoteOS Protocol
- RemoteServer Platform
