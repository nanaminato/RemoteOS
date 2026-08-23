# RemoteOS Protocol 通信协议层

> 本文档定义 RemoteOS Client↔Server 通信协议契约层 `Shared/RemoteOS.Protocol`：模块结构、序列化约定、REST 端点、SignalR Hub 契约、认证集成方式。
>
> - 架构原则见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md) §4.8
> - 当前实现状态见 [`RemoteOS.md`](../README.md) §4.8
> - 登录与身份见 [`RemoteOS.Authentication.md`](../platform/RemoteOS.Authentication.md)
> - Workspace 模型见 [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)

---

## 1. 定位与边界

`RemoteOS.Protocol` 是 Client↔Server **唯一**通信契约层。所有 Client/Server 通信必须经过 Protocol，禁止业务代码直接调用 HTTP / WebSocket / TCP。

**包含**：DTO、Message、API Contract（路由常量）、SignalR Hub 接口、序列化约定。

**不包含**（边界）：
- 客户端代理实现（`HubConnection` 包装、typed HttpClient）→ 位于 `RemoteOS.Client`
- Server 端 Hub 实现与端点实现 → 位于 `RemoteOS.Server`
- Server 端 OS 抽象（`IIdentityProvider` / `IFileSystem` 等）→ 位于 `RemoteOS.Server` 内部

Protocol 程序集**零 PackageReference**，不引用 Core（避免线协议与 Core 版本耦合）。

---

## 2. 通信框架

| 通道 | 用途 |
|------|------|
| **REST API**（`/api/v1/*`） | 请求-响应：登录、刷新令牌、Workspace/Session/Device 资源 CRUD、控制权请求、桌面状态全量读写、**文件管理**（`/api/v1/files/*`：drives/list/info/download/directory/delete/rename/move/copy/upload）、**浏览器**（`/api/v1/browser/*`：书签/历史 CRUD + `BrowserSettings` GET/PUT）、**Workspace 偏好**（`/api/v1/workspaces/{id}/preferences` GET/PUT）、**系统监控**（`/api/v1/system/*`：metrics/processes/processes/{id}） |
| **SignalR Hub**（`/hubs/workspace`） | 实时双向：桌面状态增量广播、设备上下线通知、控制权变更通知、Session/Workspace 状态变更通知 |
| **SignalR Hub**（`/hubs/terminals`） | 实时双向：远端 PTY 字节流中继（输入/输出/尺寸/退出/会话附加/列表/手动终止）。PTY 由 `TerminalSessionManager` 持有，与 Hub 连接解耦 |

SignalR 内部走 WebSocket（不可用降级 SSE/长轮询），**不裸用 WebSocket**。Workspace 多设备通过 SignalR Group（一个 Workspace 一个 Group）广播。Terminal Hub 不启用 `WithAutomaticReconnect`（自动重连后服务端不会自动重新附加会话），恢复路径是"再次登录打开终端 → 重新 `Start(Attach)` → 回放 1MB 缓冲快照"。

---

## 3. 模块结构

```text
Shared/RemoteOS.Protocol/
├── Common/        # PlatformKind、RemoteOsEndpoints、ProblemDetails、RemoteOsJsonOptions
├── Identity/      # UserDto、AuthTokens、LoginRequest/Response、RefreshToken、Logout、AuthApiRoutes
├── Workspace/     # WorkspaceDto、SessionDto、DeviceDto、ControllerLeaseInfo、3 enum、WorkspacePreferencesDto、DefaultAppMappingDto、WorkspaceApiRoutes
├── Desktop/       # DesktopStateDto/Patch、IconPositionDto、WallpaperDto、ThemeKind
├── Files/         # FileSystemEntryType/Dto、FileEntryDto、DirectoryDto、DriveDto、Rename/Move/CopyRequest、FileApiRoutes
├── Browser/       # BookmarkDto、HistoryEntryDto、Create*Request、BrowserSettingsDto、BrowserApiRoutes
├── SystemMonitor/ # SystemMetricsDto、Cpu/Memory/Disk/Network/GpuUsageDto、ProcessInfoDto、KillProcessResultDto、SystemMonitorApiRoutes
├── Certificates/  # V1：证书、挑战类型、密钥算法、operation DTO 与 CertificateApiRoutes
├── WebServers/    # V1：Nginx 实例/状态/配置测试/集成、operation DTO 与 WebServerApiRoutes
└── Hubs/          # Workspace Hub（IWorkspaceHubClient/Methods/Events、JoinWorkspaceRequest、事件参数）
                  # + Terminal Hub（ITerminalHubClient、TerminalHubMethods/Events、StartTerminalRequest、AttachTerminalResponse、TerminalSessionInfo）
```

命名空间：`RemoteOS.Protocol.{Common,Identity,Workspace,Desktop,Files,Browser,SystemMonitor,Hubs}`。

DTO 风格：`sealed record` + 主构造 + `[property: JsonPropertyName]`，对齐 `Framework/RemoteOS.Core` 风格。ID 用 `Guid`，时间用 `DateTimeOffset`，状态用 `enum`。

---

## 4. 序列化约定

`RemoteOsJsonOptions.Default` 统一序列化：
- `JsonSerializerDefaults.Web`：camelCase + 大小写不敏感
- `JsonStringEnumConverter`：枚举序列化为 camelCase 字符串（如 `"linux"`、`"running"`、`"controller"`）
- 时间：`DateTimeOffset` → ISO 8601

Server MVC（`AddControllers().AddJsonOptions`）与 SignalR（`AddSignalR().AddJsonProtocol`）共用此配置。Client Http 也用同一份 options 反序列化。

所有 DTO 公开成员显式标注 `[property: JsonPropertyName("camelCaseName")]`，钉死线协议，避免 C# 重命名导致线协议破坏。

---

## 5. REST 端点

路径前缀 `/api/v1`，错误统一返回 `ProblemDetails`（RFC 7807 子集）。路由常量集中在 `AuthApiRoutes` / `WorkspaceApiRoutes`。

### 认证
| 方法 | 路径 | 请求 | 响应 | 认证 |
|---|---|---|---|---|
| POST | `/api/v1/auth/login` | `LoginRequest` | `LoginResponse` | 无 |
| POST | `/api/v1/auth/refresh` | `RefreshTokenRequest` | `RefreshTokenResponse` | 无 |
| POST | `/api/v1/auth/logout` | `LogoutRequest` | 204 | JWT |
| GET | `/api/v1/auth/me` | — | `UserDto` | JWT |

### Workspace
| 方法 | 路径 | 请求 | 响应 | 认证 |
|---|---|---|---|---|
| GET | `/api/v1/workspaces` | — | `WorkspaceDto[]` | JWT |
| GET | `/api/v1/workspaces/{id}` | — | `WorkspaceDto` | JWT |
| POST | `/api/v1/workspaces` | `CreateWorkspaceRequest` | `WorkspaceDto` | JWT |
| GET | `/api/v1/workspaces/{id}/sessions` | — | `SessionDto[]` | JWT |
| GET | `/api/v1/workspaces/{id}/devices` | — | `DeviceDto[]` | JWT |
| GET | `/api/v1/workspaces/{id}/desktop` | — | `DesktopStateDto` | JWT |
| PUT | `/api/v1/workspaces/{id}/desktop` | `DesktopStatePatch` | `DesktopStateDto` | JWT（仅 Controller） |
| POST | `/api/v1/workspaces/{id}/control/request` | `RequestControlRequest` | `ControllerLeaseInfo` / 409 | JWT |
| POST | `/api/v1/workspaces/{id}/control/release` | — | 204 | JWT |
| POST | `/api/v1/devices` | `RegisterDeviceRequest` | `DeviceDto` | JWT |

### Files（文件管理）
路由常量见 `FileApiRoutes`。Server 以宿主 OS 进程身份执行 `System.IO`，复用宿主用户/权限（不另建 ACL）。详见 [`RemoteOS.Explorer.md`](../applications/RemoteOS.Explorer.md)。

| 方法 | 路径 | 请求 | 响应 | 认证 |
|---|---|---|---|---|
| GET | `/api/v1/files/drives` | — | `DriveDto[]` | JWT |
| GET | `/api/v1/files/special` | — | `SpecialLocationDto[]`（仅返回存在的特殊目录） | JWT |
| GET | `/api/v1/files/list` | query: `path`（空=盘符根） | `DirectoryDto` | JWT |
| GET | `/api/v1/files/info` | query: `path` | `FileSystemEntryDto` | JWT |
| GET | `/api/v1/files/download` | query: `path` | 字节流 | JWT |
| GET | `/api/v1/files/content` | query: `path` | 原始文件字节流 | JWT |
| PUT | `/api/v1/files/content` | query: `path` + 请求体字节流 | `FileEntryDto` | JWT |
| GET | `/api/v1/files/properties` | query: `path` | `FilePropertiesDto` | JWT |
| PUT | `/api/v1/files/permissions` | `UpdateUnixPermissionsRequest` | `FilePropertiesDto` | JWT |
| POST | `/api/v1/files/directory` | query: `path` | `FileSystemEntryDto`（201） | JWT |
| DELETE | `/api/v1/files` | query: `path`（目录递归） | 204 | JWT |
| POST | `/api/v1/files/rename` | `RenameRequest` | `FileSystemEntryDto` | JWT |
| POST | `/api/v1/files/move` | `MoveRequest` | `FileSystemEntryDto` | JWT |
| POST | `/api/v1/files/copy` | `CopyRequest` | `FileSystemEntryDto` | JWT |
| POST | `/api/v1/files/upload` | query: `path` + multipart/form-data | `FileEntryDto` | JWT |

### Browser（浏览器）
路由常量见 `BrowserApiRoutes`。书签/历史按 JWT `sub` claim 取 userId 隔离；`BrowserSettings` 随 Workspace 持久化。详见 [`RemoteOS.Browser.md`](../applications/RemoteOS.Browser.md)。

| 方法 | 路径 | 请求 | 响应 | 认证 |
|---|---|---|---|---|
| GET | `/api/v1/browser/settings` | — | `BrowserSettingsDto` | JWT |
| PUT | `/api/v1/browser/settings` | `BrowserSettingsDto` | `BrowserSettingsDto` | JWT |
| GET | `/api/v1/browser/bookmarks` | — | `BookmarkDto[]` | JWT |
| POST | `/api/v1/browser/bookmarks` | `CreateBookmarkRequest` | `BookmarkDto`（201） | JWT |
| DELETE | `/api/v1/browser/bookmarks/{id}` | — | 204 | JWT |
| DELETE | `/api/v1/browser/bookmarks` | — | `{ removed }` | JWT |
| GET | `/api/v1/browser/history?limit=` | query: `limit`（默认 100，上限 1000） | `HistoryEntryDto[]` | JWT |
| POST | `/api/v1/browser/history` | `CreateHistoryEntryRequest` | `HistoryEntryDto`（201） | JWT |
| DELETE | `/api/v1/browser/history/{id}` | — | 204 | JWT |
| DELETE | `/api/v1/browser/history` | — | `{ removed }` | JWT |

### Workspace Preferences（设置中心偏好）
路由常量见 `WorkspaceApiRoutes.Preferences`。复用 `FindAuthorizedWorkspace` 按 JWT `sub` 校验 Workspace 归属。详见 [`RemoteOS.Settings.md`](../desktop/RemoteOS.Settings.md)。

| 方法 | 路径 | 请求 | 响应 | 认证 |
|---|---|---|---|---|
| GET | `/api/v1/workspaces/{id}/preferences` | — | `WorkspacePreferencesDto` | JWT（按归属） |
| PUT | `/api/v1/workspaces/{id}/preferences` | `WorkspacePreferencesDto` | `WorkspacePreferencesDto`（归一化） | JWT（按归属） |

### SystemMonitor（任务管理器）
路由常量见 `SystemMonitorApiRoutes`。服务端 `ISystemMetricsProvider` 以宿主 OS 进程身份实时采集，**不持久化**。详见 [`RemoteOS.TaskManager.md`](../applications/RemoteOS.TaskManager.md)。

| 方法 | 路径 | 请求 | 响应 | 认证 |
|---|---|---|---|---|
| GET | `/api/v1/system/metrics` | — | `SystemMetricsDto` | JWT |
| GET | `/api/v1/system/processes` | — | `ProcessInfoDto[]` | JWT |
| DELETE | `/api/v1/system/processes/{id}?force=` | query: `force`（可选） | `KillProcessResultDto` | JWT |

### Certificates / WebServers（V1 后端）

证书与 Web Server 的 HostGlobal 后端已实现；具体资源模型见 [`RemoteOS.CertificateManager.md`](../applications/RemoteOS.CertificateManager.md) 与 [`RemoteOS.WebServerManager.Design.md`](../applications/RemoteOS.WebServerManager.Design.md)。证书 API 提供元数据读取、预检、签发、续期、Kestrel 部署、删除、撤销和 operation 查询/取消；Web Server API 提供 Nginx 发现、状态、配置测试、最小集成、重载和 operation 查询/取消。所有变更请求：

- 所有变更请求携带 `Idempotency-Key`，返回 `OperationDto`（操作 ID、状态、阶段、稳定问题码、时间、可选快照 ID）。
- `CertificateApiRoutes` 与 `WebServerApiRoutes` 只定义 `/api/v1` 路径常量；Endpoint、Client 和 UI 不重复字面量。
- 当前单机管理员模式下，资源为 HostGlobal，不引入 User/Workspace 路径参数；需要管理员运行状态才能执行变更。
- Operation 查询、取消和后续进度事件使用 Protocol 契约，不能让 UI 通过日志文本推断状态。

---

## 6. SignalR Hub 契约

Hub 路径 `/hubs/workspace`。Server 端实现 `WorkspaceHub : Hub<IWorkspaceHubClient>` 获得编译期校验。

### Client → Server（invoke，方法名见 `WorkspaceHubMethods`）
| 方法 | 参数 | 返回 | 仅 Controller |
|---|---|---|---|
| `JoinWorkspace` | `JoinWorkspaceRequest` | `WorkspaceSnapshotDto` | 否 |
| `LeaveWorkspace` | — | void | 否 |
| `SendDesktopStateChange` | `DesktopStatePatch` | void | 是 |
| `RequestControl` | `RequestControlRequest` | `ControllerLeaseInfo` | 否 |
| `ReleaseControl` | — | void | 是 |
| `Heartbeat` | — | void | 否 |

### Server → Client（on，事件名见 `WorkspaceHubEvents`，接口 `IWorkspaceHubClient`）
- `OnDesktopStateChanged(DesktopStatePatch)`
- `OnControllerChanged(ControllerChangedEventArgs)`
- `OnDeviceConnected(DevicePresenceEventArgs)`
- `OnDeviceDisconnected(DevicePresenceEventArgs)`
- `OnSessionUpdated(SessionDto)`
- `OnWorkspaceStateChanged(WorkspaceState)`

**未设计 `SendInput`**：RemoteOS 是状态同步模式，Controller 输入通过本地应用状态变更 + 状态同步体现，不在 workspace hub 传原始键鼠。

### Terminal Hub（`/hubs/terminals`）

远端 PTY 字节流中继。Server 端实现 `TerminalHub : Hub<ITerminalHubClient>`，PTY 由 `TerminalSessionManager`（Singleton）持有，与 Hub 连接解耦——连接断开仅 `Detach`，**保留 PTY**。详见 [`RemoteOS.Terminal.md`](../applications/RemoteOS.Terminal.md)。

#### Client → Server（invoke，方法名见 `TerminalHubMethods`）
| 方法 | 参数 | 返回 | 说明 |
|---|---|---|---|
| `Start` | `StartTerminalRequest req, string? sessionId = null` | `AttachTerminalResponse {SessionId, Created}` | sessionId 命中且属于当前用户且未退出则**附加**（先回放 1MB 缓冲快照），否则**新建** PTY 会话 |
| `Input` | `byte[]` | void | 转发到 `session.Pty.Write(data)` |
| `Resize` | `int cols, int rows, int widthPixels, int heightPixels` | void | 转发到 `session.Pty.Resize(...)` |
| `Close` | — | void | `manager.Remove` —— **手动终止**（杀 PTY），对应关闭终端窗口 / "断开"按钮 |
| `ListSessions` | — | `TerminalSessionInfo[]` | 返回当前用户全部终端会话摘要（多实例） |

#### Server → Client（on，事件名见 `TerminalHubEvents`，接口 `ITerminalHubClient`）
- `OnOutput(byte[] data)`：PTY 输出字节（始终追加进 1MB 环形缓冲；有附加连接时经 `IHubContext` 转发）
- `OnProcessExited(int exitCode)`：子进程退出

> **方法名对齐**：Server Hub 方法名必须与 `TerminalHubMethods` 常量完全一致（`Start` 非 `StartTerminal`），否则 SignalR 运行时找不到方法。`OnDisconnectedAsync` 调 `session.Detach(Context.ConnectionId)` 保留 PTY；仅显式 `Close` 才杀。`TerminalUserIdProvider`（`IUserIdProvider`）以 JWT `sub` claim 作 `Context.UserIdentifier`，按用户过滤会话。

---

## 7. 认证集成

- 登录返回 `AuthTokens`（AccessToken + RefreshToken）
- REST：`Authorization: Bearer <accessToken>`
- SignalR：连接时携带 token（query string 或 header），Server 端 `IUserIdProvider` + JWT 中间件解析，连接建立时绑定到 Session/Device/Workspace 并加入对应 Group
- Controller/Observer 协调在 SignalR Hub 层完成（`RequestControl` / `ReleaseControl` + `OnControllerChanged` 广播）

---

## 8. Terminal 传输（已实现）

RemoteTerminal 的 PTY 流传输**已在 Protocol 契约内**，走 SignalR Hub `/hubs/terminals`（见 §6 Terminal Hub）。契约文件位于 `Shared/RemoteOS.Protocol/Hubs/`：

| 文件 | 职责 |
|------|------|
| `ITerminalHubClient.cs` | server→client 接口（`OnOutput`/`OnProcessExited`） |
| `TerminalHubEvents.cs` | server→client 事件名常量 |
| `TerminalHubMethods.cs` | client→server 方法名常量（`Start`/`Input`/`Resize`/`Close`/`ListSessions`） |
| `StartTerminalRequest.cs` | 启动请求 DTO（columns/rows/widthPixels/heightPixels/shell/workingDirectory） |
| `AttachTerminalResponse.cs` | `Start` 返回值（`SessionId` + `Created`） |
| `TerminalSessionInfo.cs` | 会话摘要 DTO（`ListSessions` 用） |

**实现要点**：
- RoyalTerminal（`royalapplications/RoyalTerminal`）是传输无关的终端 UI 栈，通过 `ITerminalTransport` 抽象开放传输方式。RemoteOS 用 `RoyalApps.RoyalTerminal.Avalonia` 作为终端控件 + 自实现 `SignalRTerminalTransport`（`ITerminalTransport`）适配器，位于 `Client/RemoteOS.Client/Apps/`。
- 传输层未引入裸 WebSocket 端点（选 SignalR：JWT + 强类型 Hub + 一次性连接拉取列表）。
- 不启用 `WithAutomaticReconnect`（自动重连后服务端不会自动重新附加会话）；恢复路径是"再次登录打开终端 → 重新 `Start(Attach)` → 回放 1MB 缓冲快照"。
- `MaximumReceiveMessageSize = null` 解除 SignalR 默认 32KB 上限，允许大块 PTY 输出与 1MB 缓冲快照单帧传输。

完整实现细节（Hub 行为、断开语义、会话生命周期、焦点修复等）见 [`RemoteOS.Terminal.md`](../applications/RemoteOS.Terminal.md)。

---

## 9. AI Agent Rules

修改 Protocol 层时：

**必须**：
- 保持 Protocol 零 PackageReference（纯契约）
- 所有 DTO 公开成员加 `[property: JsonPropertyName]`
- 路由字符串集中在 `*ApiRoutes` 静态类，不散落
- Hub 方法名/事件名用 `WorkspaceHubMethods` / `WorkspaceHubEvents` 常量，不用字面量
- 枚举值与文档（Authentication.md / Security.md / Workspace.md）一致

**禁止**：
- 在 Protocol 引入 `Microsoft.AspNetCore.SignalR.Client` / `HttpClient` 等实现包
- 在 Protocol 引用 `RemoteOS.Core`（线协议与 Core 解耦）
- 业务代码直接调用 HTTP / WebSocket（必须经 Protocol 契约）
- 把 Server 端 OS 抽象（`IIdentityProvider` 等）放进 Protocol

---

## 10. 相关文档

| 文档 | 用途 |
|------|------|
| [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md) | 模块定位、依赖约束、架构原则 |
| [`RemoteOS.Authentication.md`](../platform/RemoteOS.Authentication.md) | 登录、身份模型、User/Session/Device 表 |
| [`RemoteOS.Login.md`](../platform/RemoteOS.Login.md) | 登录模块：auth 端点、JWT、IIdentityProvider |
| [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md) | Workspace 生命周期、Controller/Observer |
| [`RemoteOS.Security.md`](../platform/RemoteOS.Security.md) | Session 安全、权限提升 |
| [`RemoteOS.Terminal.md`](../applications/RemoteOS.Terminal.md) | Terminal Hub 实现、持久会话、断开语义 |
| [`RemoteOS.Explorer.md`](../applications/RemoteOS.Explorer.md) | 文件管理端点实现、宿主 OS 权限复用 |
| [`RemoteOS.Browser.md`](../applications/RemoteOS.Browser.md) | 浏览器端点实现、BrowserSettings 持久化 |
| [`RemoteOS.Settings.md`](../desktop/RemoteOS.Settings.md) | Workspace 偏好端点实现、PreferencesSync 多设备同步 |
| [`RemoteOS.TaskManager.md`](../applications/RemoteOS.TaskManager.md) | 系统监控端点实现、跨平台 ISystemMetricsProvider |
| [`RemoteOS.Storage.md`](../platform/RemoteOS.Storage.md) | EF Core + SQLite 持久化、表结构 |
| [`RemoteOS.md`](../README.md) | 项目结构、当前进度 |
