# RemoteOS Terminal 模块设计

> 内置终端应用：基于 [RoyalTerminal](https://github.com/royalapplications/RoyalTerminal) NuGet 包引入终端能力，支持 **Remote Mode**（SignalR 远端 PTY）与 **Local Mode**（本地 PTY 回退）。
>
> - 架构原则见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)（§6 Application Execution Model / §6.2 Remote Service Application）
> - 项目当前状态见 [`RemoteOS.md`](./RemoteOS.md)（§7 RemoteTerminal）
> - 桌面外壳与窗口管理见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)
> - 登录与身份认证见 [`RemoteOS.Login.md`](./RemoteOS.Login.md)（终端 Remote Mode 复用 `IAuthSession` JWT）

---

## 1. 定位

RemoteTerminal 是 RemoteOS 的内置应用之一，遵循架构 §6 的两类应用模型：

- **Remote Mode**（本次实现）：PTY 运行于 `RemoteOS.Server`，经 **SignalR Hub** 流式传输到 Client。属 §6.2 Remote Service Application。客户端认证后（`IAuthSession.State == Authenticated`）自动走 Remote Mode，JWT 通过 SignalR `AccessTokenProvider` 传递。
- **Local Mode**（回退）：运行于 `RemoteOS.Client`，本地 PTY 启动平台默认 shell。属 §6.1 Local Application。当未登录（dev 调试）时自动回退。

**传输选型：SignalR**。RoyalTerminal 的终端栈是传输方式无关的（`ITerminalTransport` 抽象），SignalR 与裸 WebSocket 均可行。本项目选择 SignalR 的理由：
1. 已有 `Microsoft.AspNetCore.SignalR.Client` 基础设施（与后续 Workspace Hub 同栈）。
2. 自动重连、JWT 鉴权、强类型 Hub 契约（`Hub<ITerminalHubClient>`）。
3. 无需额外引入裸 WebSocket 端点与手写握手机制。

---

## 2. 包与集成方式

### 2.1 NuGet 包

| 包 | 版本 | 用途 |
|----|------|------|
| `RoyalApps.RoyalTerminal.Avalonia` | 0.4.0 | Avalonia 终端控件 `TerminalControl` + 默认会话组合（托管 VT 处理器 + 平台 PTY 工厂），目标 net10.0 |
| `RoyalApps.RoyalTerminal.Terminal.Pty.Platform` | 0.4.0 | Server 端 PTY 工厂（Windows ConPTY / Unix forkpty），Terminal Hub 哑中继用 |
| `Microsoft.AspNetCore.SignalR.Client` | 10.0.0 | Client 端 SignalR 连接（`HubConnection`） |

- 中心化包管理：版本声明在 [`Directory.Packages.props`](../Directory.Packages.props)，csproj 仅 `PackageReference`（不带 Version）。
- `RoyalApps.RoyalTerminal.Avalonia` 传递依赖 `Terminal.Pty.Platform`、`Terminal.Transport.Ssh.Abstractions`、`Terminal.Transport.Ssh.SshNet` 等，Client 端无需单独引用 PTY 平台包（仅 Server 需要）。
- `AddSignalR` 由 `Microsoft.NET.Sdk.Web` 隐式 FrameworkReference 提供，Server 端无需额外 NuGet。
- **不引入** `RoyalApps.RoyalTerminal.Avalonia.App`（自带 MainWindow / 标签 / 标题栏，会与 RemoteOS 的 WindowManager 冲突）。
- **不引入** native Ghostty VT 包（`Terminal.Vt.Ghostty` + RID 相关原生资产），MVP 使用托管 VT 处理器。

### 2.2 嵌入而非替换 Shell

`TerminalControl` 作为普通 `UserControl` 内容塞进 `RemoteWindow`，与 Notepad / Settings 同构。但与 Notepad 不同的是，`TerminalControl` **在代码后台创建**（非 XAML），因为需要通过 9 参数构造函数注入自定义传输工厂：

```text
TerminalApp (RemoteApplicationBase)
    |
    AppContext.ShowWindow("Terminal", view)
    |
    WindowManager.Create → RemoteWindow
    |
    TerminalView (UserControl)
    ├── 工具条（Restart / Clear / 状态栏）
    └── TerminalHost (Grid)
        └── TerminalControl (code-behind 创建, 9-param ctor)
            └── TerminalTransportFactory = SignalRTransportFactory
```

### 2.3 TerminalControl 构造（9 参数构造函数）

`TerminalControl.TerminalTransportFactory` 是**只读属性**，只能在构造时注入。XAML 声明使用的是无参构造函数（创建默认 `CompositeTerminalTransportFactory`），无法在运行时替换。因此 `TerminalView.axaml.cs` 使用 9 参数构造函数：

```csharp
var control = new TerminalControl(
    new TerminalSessionService(),          // ITerminalSessionService
    new DefaultTerminalInputAdapter(),     // ITerminalInputAdapter
    new DefaultTerminalSelectionService(), // ITerminalSelectionService
    new DefaultTerminalScrollService(),    // ITerminalScrollService
    new DefaultVtProcessorFactory(),       // IVtProcessorFactory
    new DefaultPtyFactory(),               // IPtyFactory (Local Mode 回退用)
    new NullSshCredentialProvider(),       // ISshCredentialProvider (包内置, no-op)
    new KnownHostsSshHostKeyValidator(),   // ISshHostKeyValidator (包内置)
    transportFactory);                     // ITerminalTransportFactory — 我们的 SignalRTransportFactory
```

- 前两行服务与无参构造函数的默认值完全一致（镜像 `TerminalControl()` 源码）。
- `NullSshCredentialProvider` / `KnownHostsSshHostKeyValidator` 来自包传递依赖 `Terminal.Transport.Ssh.SshNet` / `Terminal.Transport.Ssh.Abstractions`，namespace 为 `RoyalTerminal.Terminal.Transport.Ssh` / `.SshNet`。SSH 传输在 RemoteOS 不使用（Remote Mode 走 SignalR），这两个 provider 仅满足构造函数签名。
- 构造后设置 `Focusable = true`、`Columns`、`Rows`、`ScrollbackLimit`、`TerminalFontSize`（原先在 XAML 中设置的属性）。

---

## 3. 架构：Remote Mode（SignalR）

### 3.1 数据流

```text
┌───────────── Client (RemoteOS.Client) ─────────────┐    ┌──────── Server (RemoteOS.Server) ────────┐
│                                                    │    │                                          │
│  TerminalControl                                   │    │  TerminalHub (Hub<ITerminalHubClient>)   │
│    ├── VT 处理器 (托管, client-side)               │    │    ├── IPty (ConPTY/forkpty)             │
│    ├── Skia 渲染                                   │    │    └── Shell (powershell/bash)           │
│    └── TerminalTransportFactory                    │    │                                          │
│         └── SignalRTransportFactory                │    │  Start(req)  → pty.Start(shell,cols,rows)│
│              └── SignalRTerminalTransport          │    │  Input(data) → pty.Write(data)           │
│                   ├── HubConnection                │◄───┤  Resize(c,r) → pty.Resize(c,r)           │
│                   │   OnOutput(byte[])  ◄──────────┤────┤  pty.DataReceived → Clients.Caller.OnOutput│
│                   │   OnProcessExited(int) ◄───────┤────┤  pty.ProcessExited → OnProcessExited+Dispose│
│                   │   Start(Input/Resize/Close) ──►│────┤  Close()      → DisposePty()             │
│                   │   [JWT via AccessTokenProvider]│    │  OnDisconnected → DisposePty()           │
│                   └── DataReceived/ProcessExited   │    │                                          │
│                        (事件, 传给 TerminalControl)  │    │  IPtyFactory (DefaultPtyFactory)        │
│                                                    │    │                                          │
└────────────────────────────────────────────────────┘    └──────────────────────────────────────────┘
```

**核心设计：服务端是 PTY 哑中继**。Server 端只做三件事：创建 PTY、转发输入字节、回传输出字节。VT 解析（标题/响铃/光标/颜色/滚动）全部在客户端的 `TerminalControl` 内完成。这使得服务端逻辑极简，且 VT 引擎升级不影响服务端。

### 3.2 Protocol 契约层

`Shared/RemoteOS.Protocol/Hubs/` 下定义了 Terminal Hub 的完整契约（零 PackageReference，DTO 用 sealed record + `[property: JsonPropertyName]`）：

| 文件 | 职责 |
|------|------|
| `ITerminalHubClient.cs` | server→client 接口：`OnOutput(byte[])`、`OnProcessExited(int)`。Server 端 `Hub<ITerminalHubClient>` 获编译期校验；Client 端 `HubConnection.On<T>` 注册回调 |
| `TerminalHubEvents.cs` | server→client 事件名常量（`nameof(ITerminalHubClient.OnOutput)` 等），Client 端 `HubConnection.On` 用 |
| `TerminalHubMethods.cs` | client→server invoke 方法名常量（`Start`/`Input`/`Resize`/`Close`） |
| `StartTerminalRequest.cs` | 启动终端请求 DTO（columns/rows/widthPixels/heightPixels/shell/workingDirectory） |

> **方法名对齐**：Server Hub 方法名必须与 `TerminalHubMethods` 常量完全一致。`Start` 方法在 Hub 上命名为 `Start`（非 `StartTerminal`），因为客户端 `InvokeAsync(TerminalHubMethods.Start)` 发送的是 `"Start"`。

### 3.3 传输层实现（Client 端）

| 文件 | 职责 |
|------|------|
| `SignalRTransportOptions.cs` | `ITerminalTransportOptions` 实现：HubUrl、Dimensions、TokenProvider/AccessToken、Shell/WorkingDirectory |
| `SignalRTerminalTransport.cs` | `ITerminalTransport` 实现：`HubConnection` 生命周期管理，桥接 `OnOutput`→`DataReceived`、`OnProcessExited`→`ProcessExited` |
| `SignalRTransportFactory.cs` | `ITerminalTransportFactory` 实现：对 `SignalRTransportOptions` 返回 SignalR 传输，其余（如 `PtyTransportOptions`）委托内部 `CompositeTerminalTransportFactory`（含 `PtyTerminalTransportProvider`，Local Mode 回退用） |

**`SignalRTransportFactory` 是自包含的**：在构造时创建内部 `CompositeTerminalTransportFactory([PtyTerminalTransportProvider()])`，无需从 `TerminalControl` 读取默认工厂。持有最近创建的 `SignalRTerminalTransport` 引用，供 `StopActiveAsync()` 主动关闭连接。

### 3.4 认证

SignalR 连接通过 `AccessTokenProvider` 携带 JWT：

```csharp
http.AccessTokenProvider = () =>
    Task.FromResult<string?>(opts.TokenProvider?.Invoke() ?? opts.AccessToken);
```

- `TokenProvider` 是 `Func<string?>`，每次连接/重连时调用以获取最新 token（从 `IAuthSession.Tokens.AccessToken` 读取）。
- Server 端 `TerminalHub` 标注 `[Authorize]`，JWT 验证由 `JwtBearer` 中间件处理（与 auth 端点同密钥/签发者）。
- 未认证时（`IAuthSession.State != Authenticated`），VM 回退到 `PtyTransportOptions`（Local Mode），不发起 SignalR 连接。

---

## 4. Server 端：TerminalHub

### 4.1 Hub 实现

`RemoteOS.Server/Hubs/TerminalHub.cs`：

- **每连接一 PTY**：`Context.Items["pty"]` 存储 `IPty` 实例，连接与 PTY 一一对应。
- **`Start(StartTerminalRequest)`**：释放旧 PTY（容错）→ `_ptyFactory.Create()` → 订阅 `DataReceived`/`ProcessExited` → `pty.Start(shell, cols, rows, cwd, env, null)`。
- **`Input(byte[])`**：`pty.Write(data, 0, data.Length)`。
- **`Resize(int, int, int, int)`**：`pty.Resize(cols, rows, widthPixels, heightPixels)`。
- **`Close()`**：`DisposePty()`（不关闭连接，允许客户端重新 `Start`）。
- **`OnDisconnectedAsync`**：`DisposePty()`（连接断开即释放 PTY，确保无残留 shell 进程）。

### 4.2 PTY 工厂与环境

- `DefaultPtyFactory`（来自 `RoyalApps.RoyalTerminal.Terminal.Pty.Platform`）：Windows 用 ConPTY，Linux 用 forkpty。
- 默认 shell：Windows `powershell`，Linux `bash`（`RuntimeInformation.IsOSPlatform` 判定）。
- 环境：继承宿主进程环境变量 + `TERM=xterm-256color`。
- 工作目录：请求中未指定时用 `Environment.SpecialFolder.UserProfile`。

### 4.3 Server 注册

`RemoteOS.Server/Program.cs`：

```csharp
builder.Services.AddSingleton<IPtyFactory, DefaultPtyFactory>();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = null); // 大输出流不截断
// ...
app.MapHub<TerminalHub>("/hubs/terminals");
```

`MaximumReceiveMessageSize = null` 解除 SignalR 默认 32KB 消息上限，允许大块 PTY 输出（如 `cat` 大文件）单帧传输。

---

## 5. 会话生命周期

| 时机 | 触发 | 动作 |
|------|------|------|
| 控件 Loaded | `TerminalView.OnLoaded` | `vm.AttachAsync(terminal, factory)` → 订阅事件 → `StartSessionAsync()` |
| 窗口关闭 | `TerminalView.OnUnloaded` | `vm.Detach()` → `factory.StopActiveAsync()`（关连接→Server `OnDisconnected` 释放 PTY）→ `terminal.StopPty()` |
| 进程退出 | `TerminalControl.ProcessExited` | 更新状态栏、置 `HasExited` |
| Restart 按钮 | `TerminalViewModel.RestartCommand` | `StopPty()` + `StopActiveAsync()` → `StartSessionAsync()` |
| Clear 按钮 | `TerminalViewModel.ClearCommand` | `terminal.ClearScrollback()` |
| 窗口 resize | `TerminalControl` 内部自动同步 | 走 transport `Resize()` → SignalR `Resize` invoke → Server `pty.Resize()` |

### 5.1 模式选择（TerminalViewModel.StartSessionAsync）

```csharp
if (_session is { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens })
{
    // Remote Mode: SignalR transport
    options = new SignalRTransportOptions(
        hubUrl: $"{url.TrimEnd('/')}/hubs/terminals",
        dimensions: dimensions,
        tokenProvider: () => _session.Tokens?.AccessToken,
        accessToken: tokens.AccessToken);
}
else
{
    // Local Mode fallback: local PTY via inner composite factory
    options = new PtyTransportOptions(
        Command: null,
        WorkingDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment: null,
        Dimensions: dimensions);
}
```

### 5.2 关键 RoyalTerminal API

| 成员 | 说明 |
|------|------|
| `TerminalControl` 9-param ctor | 注入自定义 `ITerminalTransportFactory`（`TerminalTransportFactory` 只读） |
| `TerminalControl.StartSessionAsync(ITerminalTransportOptions, CancellationToken)` | 会话入口，路由到传输工厂 |
| `TerminalControl.StopPty()` | 停止 PTY / 关闭传输（清理用） |
| `TerminalControl.ClearScrollback()` | 清滚动历史 |
| `TerminalControl.ProcessExited` | `EventHandler<int>`，int = 退出码 |
| `TerminalControl.TitleChanged` | `EventHandler<string>`，shell OSC 0/2 标题 |
| `ITerminalTransport` | 传输抽象：`StartAsync`/`SendInput`/`Resize`/`StopAsync` + `DataReceived`/`ProcessExited` 事件 |
| `ITerminalTransportFactory.Create(ITerminalTransportOptions)` | 按 options 类型选择传输 |
| `PtyTransportOptions(Command, WorkingDirectory, Environment, Dimensions)` | Local Mode 选项 |
| `TerminalSessionDimensions(Columns, Rows, WidthPixels, HeightPixels)` | 初始网格尺寸 |

---

## 6. 输入焦点修复

`TerminalControl.Focusable` 默认为 `false`，导致控件无法获取键盘焦点、无法输入。修复方案：

1. **XAML → code-behind**：构造 `TerminalControl` 后设置 `control.Focusable = true`。
2. **初始聚焦**：`TerminalView.OnLoaded` 中 `FocusTerminal()`。
3. **点击重聚焦**：`TerminalControl.PointerPressed` → `Dispatcher.UIThread.Post(FocusTerminal)`（延迟到冒泡事件之后，避免 `RemoteWindow` 的 `WindowManager.Focus` 抢回焦点）。

> `RemoteWindow` 在每次 pointer press 时通过 `WindowManager.Focus` → `window.View.Focus()` 将自身置于最前。若不延迟聚焦，键盘焦点会落在 `RemoteWindow` 而非 `TerminalControl`。

---

## 7. 文件清单

### Client 端

| 文件 | 职责 |
|------|------|
| `Client/RemoteOS.Client/Apps/TerminalApp.cs` | `RemoteApplicationBase` 实现，Manifest + `Activate` 开窗，注入 `IAuthSession` |
| `Client/RemoteOS.Client/Apps/TerminalView.axaml` | UserControl，工具条 + `TerminalHost`（Grid 占位） |
| `Client/RemoteOS.Client/Apps/TerminalView.axaml.cs` | code-behind 创建 `TerminalControl`（9-param ctor）+ 焦点管理 |
| `Client/RemoteOS.Client/Apps/TerminalViewModel.cs` | 会话状态、模式选择（Remote/Local）、Restart/Clear 命令 |
| `Client/RemoteOS.Client/Apps/SignalRTransportOptions.cs` | SignalR 传输选项 DTO |
| `Client/RemoteOS.Client/Apps/SignalRTerminalTransport.cs` | `ITerminalTransport` 的 SignalR 实现 |
| `Client/RemoteOS.Client/Apps/SignalRTransportFactory.cs` | 传输工厂：SignalR + 本地 PTY 回退 |

### Server 端

| 文件 | 职责 |
|------|------|
| `RemoteOS.Server/Hubs/TerminalHub.cs` | PTY 哑中继 Hub（Start/Input/Resize/Close + OnDisconnected 清理） |
| `RemoteOS.Server/Program.cs` | 注册 `IPtyFactory` + `AddSignalR` + `MapHub<TerminalHub>` |

### Protocol 契约层

| 文件 | 职责 |
|------|------|
| `Shared/RemoteOS.Protocol/Hubs/ITerminalHubClient.cs` | server→client 接口 |
| `Shared/RemoteOS.Protocol/Hubs/TerminalHubEvents.cs` | server→client 事件名常量 |
| `Shared/RemoteOS.Protocol/Hubs/TerminalHubMethods.cs` | client→server 方法名常量 |
| `Shared/RemoteOS.Protocol/Hubs/StartTerminalRequest.cs` | 启动请求 DTO |

### 包管理

| 文件 | 职责 |
|------|------|
| `Directory.Packages.props` | `RoyalApps.RoyalTerminal.Avalonia` / `.Terminal.Pty.Platform` / `Microsoft.AspNetCore.SignalR.Client` 版本声明 |
| `Client/RemoteOS.Client/RemoteOS.Client.csproj` | Client 引用 `RoyalApps.RoyalTerminal.Avalonia` + `Microsoft.AspNetCore.SignalR.Client` |
| `RemoteOS.Server/RemoteOS.Server.csproj` | Server 引用 `RoyalApps.RoyalTerminal.Terminal.Pty.Platform` |

---

## 8. AI Agent 规则

实现与维护终端模块时必须遵守：

- **Remote Mode 是默认模式**：认证后（`IAuthSession.State == Authenticated`）自动走 SignalR 远端 PTY。Local Mode 仅作未登录时的 dev 回退，不得作为正式运行模式。
- **传输用 SignalR，禁止裸 WebSocket**：RoyalTerminal 传输抽象（`ITerminalTransport`）是传输方式无关的，本项目选择 SignalR（自动重连 + JWT + 强类型 Hub）。禁止为终端单独引入裸 WebSocket 端点。
- **`TerminalControl` 在 code-behind 创建（9-param ctor）**：`TerminalTransportFactory` 是只读属性，只能通过构造函数注入 `SignalRTransportFactory`。禁止在 XAML 中声明 `TerminalControl` 后尝试运行时替换传输工厂。
- **服务端是 PTY 哑中继**：`TerminalHub` 只做创建/输入/输出/退出/尺寸/清理，**不做 VT 解析**。VT 渲染（标题/响铃/光标/颜色）全部在客户端 `TerminalControl` 完成。服务端不得引入 VT 处理器。
- **Hub 方法名必须与 `TerminalHubMethods` 常量一致**：Server Hub 方法 `Start`/`Input`/`Resize`/`Close` 必须与 `TerminalHubMethods` 中的常量值（`nameof`）完全匹配，否则 SignalR 运行时找不到方法。
- **连接断开即释放 PTY**：`TerminalHub.OnDisconnectedAsync` 必须调用 `DisposePty()`。禁止在服务端保留无连接的 PTY（会导致 shell 进程泄漏）。
- **JWT 鉴权**：`TerminalHub` 标注 `[Authorize]`；Client 端通过 `AccessTokenProvider` 从 `IAuthSession.Tokens.AccessToken` 获取 token。禁止未认证连接。
- **嵌入 `TerminalControl`，禁止引入 `RoyalApps.RoyalTerminal.Avalonia.App`**：RemoteOS 自有 WindowManager / RemoteWindow / Taskbar，不能被 RoyalTerminal 的外壳接管。
- **焦点修复不可移除**：`TerminalControl.Focusable = true`（code-behind 设置）+ `PointerPressed` 延迟聚焦是输入正常的前提。`RemoteWindow` 的 `WindowManager.Focus` 会抢回焦点，必须用 `Dispatcher.UIThread.Post` 延迟。
- **会话清理走 View 的 `OnUnloaded`**：SDK 无 `Deactivate` 钩子；`RemoteWindow` 关闭 → 控件离开视觉树 → `Detach()` → `StopActiveAsync()`（关 SignalR 连接）+ `StopPty()`。
- **不引入 native Ghostty VT**（除非显式需求）：MVP 用托管 VT；引入 native 需按 RID 处理原生资产包。
- **程序集名是 `RoyalTerminal.Avalonia`**（非 NuGet 包 id `RoyalApps.RoyalTerminal.Avalonia`）；`NullSshCredentialProvider` 在 `RoyalTerminal.Terminal.Transport.Ssh.SshNet` namespace，`KnownHostsSshHostKeyValidator` 在 `RoyalTerminal.Terminal.Transport.Ssh` namespace（abstractions 程序集，传递依赖可用）。
