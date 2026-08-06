# RemoteOS Login 模块设计文档

> 本文档定义 RemoteOS 登录模块：客户端登录窗口（mstsc 风格）、传输层、认证会话、服务端 auth 端点、JWT、IIdentityProvider 抽象、内存持久化。
>
> 本文档描述**已实现的 MVP**：客户端可真实登录到本机 Server 并进入桌面。不含 SignalR Hub（桌面状态同步是独立模块）。
>
> - 通信契约见 [`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md)
> - 身份模型与认证原则见 [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md)
> - Workspace / Session / Device 模型见 [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)
> - 登录成功后的桌面外壳（连接栏、断开 = 登出）见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)
> - 整体架构见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)

---

## 1. 模块定位

### 1.1 参考 Windows mstsc

RemoteOS 登录模块参考 Windows Server 远程桌面连接工具 **mstsc** 的交互范式：启动先弹独立登录窗口，输入"地址 + 用户名 + 密码"→ 连接成功后进入桌面。

|  | mstsc | RemoteOS Login |
|---|---|---|
| 登录窗口 | 独立"远程桌面连接"窗口 | 独立 `LoginWindow`（顶层 Avalonia Window） |
| 输入项 | 计算机 + 用户名 + 密码 | Server URL + 用户名 + 密码 |
| 传输协议 | RDP（像素流） | HTTP REST（状态同步，非像素流） |
| 认证后 | 全屏远程桌面 | MainWindow 桌面 Shell |
| 凭据存储 | 默认不保存（可选 .rdp） | 可选“加密保存密码并自动登录”：Windows DPAPI / macOS Keychain / Linux Secret Service |

### 1.2 MVP 范围

**已实现**：

- 客户端：`LoginWindow` + `LoginView` + `LoginViewModel` + `IRemoteOsClient`（typed HttpClient）+ `IAuthSession`（可选记住设备）+ 启动分叉
- 服务端：`/api/v1/auth/login|refresh|logout|me` 端点 + JWT 签发 + `IIdentityProvider` 抽象 + `WindowsLogonProvider`（LogonUser 迁移）+ 内存仓储（User/Workspace/Session/Device）+ `LinuxPamProvider` 占位
- 协议：零改动（复用 Protocol 已有的 `LoginRequest`/`LoginResponse`/`AuthTokens`/`AuthApiRoutes`/`ProblemDetails`）

**非范围（未来扩展）**：

- SignalR Hub `/hubs/workspace`（桌面状态增量同步，独立模块）
- Linux PAM 真实实现
- "显示选项"折叠面板、最近连接列表
- 持久化仓储（SQLite / EF Core）

---

## 2. 登录流程时序

```text
Client (LoginViewModel)         Server (AuthEndpoints)         Host OS (LogonUser/PAM)
    |  POST /api/v1/auth/login       |                               |
    |  { LoginRequest }              |                               |
    |------------------------------>>|                               |
    |                                | IIdentityProvider.Verify(u,p) |
    |                                |------------------------------>>|
    |                                |<<------------------------------|
    |                                |   CredentialVerifyResult      |
    |                                |                               |
    |                                | 查/建 User, Workspace         |
    |                                | 查/建 Device, 新建 Session    |
    |                                | JwtTokenService.Issue         |
    |                                |   claims: sub/name/           |
    |                                |    workspace_id/device_id/    |
    |                                |    role/jti                   |
    |<<------------------------------|                               |
    |  200 LoginResponse             |                               |
    |   { User, Workspace,           |                               |
    |     Session, Device,           |                               |
    |     Tokens, AssignedRole }     |                               |
    |                                |                               |
    IAuthSession.State = Authenticated
    App 关闭 LoginWindow，打开 MainWindow（桌面）
```

失败路径：Server 返回 RFC 7807 `ProblemDetails`（错误码在 `type` URI），Client `RemoteOsClient` 抛 `RemoteOsAuthException`，`LoginViewModel` 按 `type` 映射本地化文案显示在登录窗口。

---

## 3. 客户端架构

### 3.1 启动分叉

```text
App.OnFrameworkInitializationCompleted
    |
    Bootstrapper.Build(this)  → IServiceProvider（含 IAuthSession / IRemoteOsClient / LoginViewModel / DesktopShellViewModel）
    |
    desktop.ShutdownMode = OnExplicitShutdown
    |
    new LoginWindow { DataContext = LoginViewModel }
    desktop.MainWindow = loginWindow; loginWindow.Show()
    |
    session.StateChanged += handler
        |
        State == Authenticated?
            |
            Dispatcher.UIThread.Post:
                new MainWindow { DataContext = DesktopShellViewModel }
                desktop.MainWindow = mainWindow; mainWindow.Show()
                loginWindow.Close()
                mainWindow.Closed → desktop.Shutdown()
```

`OnExplicitShutdown` 防止登录窗→桌面切换时进程提前退出。`MainWindow` 与 `DesktopShellViewModel` 在登录成功后才实例化，登录前不占用桌面资源。

### 3.2 组件关系

```text
LoginWindow (顶层 Window)
  └── LoginView (UserControl, x:DataType=LoginViewModel)
        └── LoginViewModel (CommunityToolkit.Mvvm)
              ├── [ObservableProperty] ServerUrl / Username / Password / IsConnecting / StatusMessage / ErrorMessage / HasError
              ├── [RelayCommand] ConnectCommand → ConnectAsync
              └── IAuthSession (DI 注入)

IAuthSession (AuthSession, 单例；可选记住设备)
  ├── State (Unauthenticated / Connecting / Authenticated)
  ├── ServerUrl / Tokens / CurrentUser / CurrentWorkspace / CurrentSession / CurrentDevice / AssignedRole
  ├── event StateChanged
  ├── LoginAsync(serverUrl, LoginRequest)
  ├── LogoutAsync()
  └── RefreshAsync()

IRemoteOsClient (RemoteOsClient, typed HttpClient)
  ├── LoginAsync(serverUrl, request)   → POST /api/v1/auth/login
  ├── RefreshAsync(serverUrl, refresh) → POST /api/v1/auth/refresh
  ├── LogoutAsync(serverUrl, access, refresh?) → POST /api/v1/auth/logout
  └── GetMeAsync(serverUrl, access)    → GET  /api/v1/auth/me
```

### 3.3 启动状态机

```text
Unauthenticated ──Connect──>> Connecting ──成功──>> Authenticated
                                │                       │
                                失败                     Logout
                                ↓                       ↓
                            Unauthenticated <<────── Unauthenticated
```

### 3.4 关键约束

- **不 mutate `HttpClient.BaseAddress`**：`RemoteOsClient` 每个方法接收 `serverUrl` 构造绝对 URI（`new Uri(new Uri(serverUrl), route.TrimStart('/'))`），避免 typed HttpClient 共享实例并发竞态。
- **登录窗用顶层 `Window`**，不用 `RemoteWindow`（`RemoteWindow` 必须挂在 `DesktopShellView` 的 `PART_WindowHost` Canvas，登录前桌面尚未建立）。
- **已保存连接**：客户端可加密保存多组 `Server URL + 用户名 + 密码`；登录窗保持可见，用户从下拉列表选择任意已保存项以回填凭据，再明确点击“连接”登录。密码未勾选时仅保存 `Server URL + 用户名`，不会保存 `RefreshToken` 或任何可替代密码的令牌；登出不会删除已保存连接。
- **HTTP 调用经 `IRemoteOsClient` 抽象**，业务代码不直接 `new HttpClient`（Architecture.md §4.8）。

---

## 4. 服务端架构

### 4.1 端点总览

| 方法 | 路由 | 认证 | 说明 |
|---|---|---|---|
| POST | `/api/v1/auth/login` | 无 | 凭据验证 → 签发 JWT → 返回 `LoginResponse` |
| POST | `/api/v1/auth/refresh` | 无 | RefreshToken 换新令牌对（旧 refresh 作废） |
| POST | `/api/v1/auth/logout` | JWT | 吊销 RefreshToken |
| GET | `/api/v1/auth/me` | JWT | 返回当前 `UserDto` |

### 4.2 IIdentityProvider 抽象

```text
IIdentityProvider
    |
    ├── WindowsLogonProvider  (advapi32!LogonUser, [SupportedOSPlatform("windows")])
    └── LinuxPamProvider      (占位，未来用 libpam 实现)
```

- `Verify(username, password) → CredentialVerifyResult`：委托宿主 OS 验证，9 种错误码映射（BadCredentials/NoSuchUser/AccountDisabled/AccountLockedOut/PasswordExpired/AccountExpired/AccountRestriction/InvalidInput/Unknown）。
- `GetUserInfo(username) → PlatformUserInfo`：返回 Uid（Windows: `domain\user`）/ DisplayName / HomeDirectory?。
- **平台选择**：`Program.cs` 按 `RuntimeInformation.IsOSPlatform` 注册对应 Provider。
- **服务器不存储密码**：认证仍完全委托宿主 OS（Authentication.md §17）；客户端仅在用户勾选自动登录后，才将密码交给操作系统安全存储。

`WindowsLogonProvider` 从 `Windows Server Test/Categories/Authentication/WindowsCredentialVerifier.cs` 迁移而来（已验证 LogonUser 可行）；`Windows Server Test` 项目改为引用 Server 调 `IIdentityProvider`，单一真源。

### 4.3 JWT 签发

```text
JwtTokenService.Issue(user, workspace, device, role)
    |
    AccessToken (15min, HMACSHA256)
      claims: sub=userId, name=username, workspace_id, device_id, role, jti=sessionId
    |
    RefreshToken (7d, 随机 32 字节)
      登记到 AuthSessionStore（refreshToken → sessionId/userId/workspaceId/deviceId/exp）
```

- AccessToken：REST/Hub 鉴权，`Authorization: Bearer <token>`。
- RefreshToken：换新用，一次性（刷新后旧 token 吊销）。
- `AuthSessionStore`：单例 `ConcurrentDictionary`，支持校验/吊销。
- 密钥：`appsettings.json` `Jwt:Secret`（≥32 字符），Production 启动校验非默认值。

### 4.4 内存仓储与领域模型

```text
Server.Domain.{User, Workspace, Session, Device}   (领域模型, ToDto() 映射)
    |
Server.Storage.{IUserRepository, IWorkspaceRepository, ISessionRepository, IDeviceRepository}
    |
InMemory*Repository (Singleton, ConcurrentDictionary, 重启丢失)
```

- **领域模型 vs Protocol DTO 分离**：Server 内部用领域模型，端点处手动 `ToDto()` 映射（Protocol 不污染领域逻辑）。
- **User**：按 (username, platform) 索引，One User。
- **Workspace**：按 UserId 索引，One User One Persistent（复用，不存在则建）。
- **Device**：按 (name, platform) 索引，复用，更新 LastLoginAt/ClientVersion。
- **Session**：每次登录新建（Session ≠ Workspace）。

### 4.5 login 端点流程

```text
1. 参数校验（空字段）→ 400 invalid-input
2. IIdentityProvider.Verify → 失败按错误码映射 ProblemDetails
3. GetUserInfo → 查/建 User
4. 查/建 Workspace（One User One Persistent）
5. 查/建 Device（按 name+platform 复用，更新登录信息）
6. 新建 Session（Status=Active）
7. 设 Workspace Controller（Grace Period 5min，见 Workspace.md §19）
8. JwtTokenService.Issue（首个设备 = Controller）
9. 返回 LoginResponse(user, workspace, session, device, tokens, role)
```

---

## 5. 错误处理矩阵

`ProblemDetails`（Protocol.Common）只有 `type/title/status/detail/traceId` 五字段，**无 Errors 字典**。错误码通过 RFC 7807 的 `type` URI 传递，客户端按 `type` 字符串映射本地化文案。`type` 前缀统一 `https://remoteos.app/problems/`。

| 场景 | HTTP | ProblemDetails.type | UI 文案 |
|---|---|---|---|
| 网络不可达/拒绝连接 | — | — | "无法连接到服务器：{host}" |
| 凭据错误/用户不存在 | 401 | `.../invalid-credential` | "用户名或密码错误" |
| 账户锁定 | 423 | `.../account-locked` | "账户已锁定，请联系管理员" |
| 账户禁用 | 403 | `.../account-disabled` | "账户已禁用" |
| 密码过期 | 403 | `.../password-expired` | "密码已过期，请先在服务器上修改" |
| 账户过期 | 403 | `.../account-expired` | "账户已过期" |
| 账户受限 | 403 | `.../account-restriction` | "账户登录受限" |
| 输入为空 | 400 | `.../invalid-input` | "请填写完整信息" |
| 服务器内部错误 | 500 | `.../auth-failed` | "登录失败，请稍后重试" |

`CredentialError → ProblemDetails` 映射在 `AuthEndpoints.MapCredentialErrorToProblem`；`type → UI 文案` 映射在 `LoginViewModel.MapProblemToMessage`。

---

## 6. 安全考量

- **服务器不存储宿主 OS 密码**：认证委托宿主 OS（LogonUser/PAM），服务端密码仅在校验瞬间传入，不落库、不日志。
- **自动登录的安全性**：未勾选时 `AuthSession` 不写盘；勾选时客户端仅通过平台凭据库保存密码和会话信息：Windows 为当前用户 DPAPI 加密文件，macOS 为 Keychain，Linux 为 Secret Service。不会写入配置、数据库或日志，登出会清除记录。
- **JWT 对称密钥**：Production 启动校验 `Jwt:Secret` 非默认占位值；Development 用固定开发密钥。
- **HTTPS**：Production 强制 HTTPS 重定向；Development 允许 http 方便本地测试。
- **密码字段**：`LoginView` 默认用 `TextBox PasswordChar="●"` 掩码显示，并提供“查看 / 隐藏”切换；`LoginViewModel` 不记录密码到日志。
- **RefreshToken 一次性**：刷新后旧 token 立即吊销，登出吊销 refresh。
- **最小权限**：`WindowsLogonProvider` 用 `LOGON32_LOGON_NETWORK`（轻量登录，不加载用户配置），不默认 root/Administrator。

---

## 7. Token 生命周期

```text
登录 → AccessToken(15min) + RefreshToken(7d)
          |
          AccessToken 过期 → RefreshAsync(refresh) → 新 AccessToken + 新 RefreshToken（旧 refresh 吊销）
          |
          RefreshToken 过期/失效 → 从系统安全存储读取密码并直接重新登录
          系统凭据被删除或密码已变更 → 回 LoginWindow 重新登录
          |
          登出 → LogoutAsync → 吊销 RefreshToken（AccessToken 自然过期）
```

- AccessToken TTL：15 分钟（`Jwt:AccessTokenTtl`）。
- RefreshToken TTL：7 天（`Jwt:RefreshTokenTtl`）。
- 刷新失败（refresh 过期/已吊销）→ `AuthSession.Reset()` → 状态回 `Unauthenticated`。
- **桌面外壳衔接**：登录后 `MainWindow` 的 mstsc 连接栏"关闭连接"与标题栏"关闭"均触发 `IAuthSession.LogoutAsync()` 后 `MainWindow.Close()`（MVP 断开即退出进程，不回登录窗），详见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md) §2.4。

---

## 8. MVP 边界与未来扩展

| 能力 | MVP | 未来 |
|---|---|---|
| SignalR Hub `/hubs/workspace` | 不含 | 独立模块，登录后建立 Hub 连接（携带 JWT） |
| Linux PAM | 占位 | libpam / PAM 绑定库实现 |
| 显示选项折叠面板 | 预留空 Expander | 显示大小/颜色深度/本地资源（对标 mstsc） |
| 最近连接列表 | 未实现 | 本地持久化最近 Server URL |
| 自动登录凭据 | Windows DPAPI / macOS Keychain / Linux Secret Service（勾选后启用） | Linux 桌面密钥环不可用时自动登录不可用，仍可正常手动登录 |
| 持久化仓储 | 内存 ConcurrentDictionary | SQLite + EF Core |
| 多设备控制权竞争 | 单设备 = Controller | Observer/Request Control 弹窗（Workspace.md §21） |
| Token 自动刷新拦截 | 手动 RefreshAsync | DelegatingHandler 自动刷新 + 重试 |

---

## 9. AI Agent 理解规则

实现 RemoteOS 登录模块时必须遵守：

**必须**：

- 启动顺序：先 `LoginWindow`，登录成功后才创建 `MainWindow`。
- 登录窗口用顶层 `Window`，不用 `RemoteWindow`（RemoteWindow 必须挂在 DesktopShellView 内）。
- HTTP 调用经 `IRemoteOsClient` 抽象，业务代码不直接 `new HttpClient`。
- 凭据验证经 `IIdentityProvider` 抽象，平台差异封装在 Provider 实现。
- 错误响应解析 `ProblemDetails`，用 `type` 字段做错误码映射（无 Errors 字典）。
- 未勾选“记住此计算机和用户名”时，不新增本地记录；勾选后保存 `Server URL + 用户名`。只有额外勾选“加密保存密码”时才将密码写入操作系统安全存储；不保存 RefreshToken，且启动后由用户选择目标服务器，避免在多服务器环境中错误地自动连到上一台机器。
- Server 领域模型与 Protocol DTO 分离，端点处手动 `ToDto()` 映射。
- 序列化统一用 `RemoteOsJsonOptions.Default`（camelCase + 枚举字符串）。

**禁止**：

- 在 Protocol 引入 `Microsoft.AspNetCore.*` 或 `HttpClient` 包。
- 把 `IIdentityProvider` / `CredentialVerifyResult` 放进 Protocol。
- 把密码写入日志、配置、数据库，或操作系统安全存储以外的任意文件。
- 在登录窗阶段引入 SignalR 连接（Hub 是独立模块）。
- mutate 共享 `HttpClient.BaseAddress`。
- 用 `RemoteWindow` 承载登录界面。
- 破坏现有桌面功能（MainWindow / Bootstrapper 桌面装配 / DesktopShellViewModel / 内置 App / WindowManager 仅在 App.axaml.cs 启动顺序分叉，逻辑不动）。

---

## 10. 已保存连接（mstsc 风格，多服务器）

登录窗口的“计算机”输入框同时是已保存连接的下拉列表。RemoteOS 按 **Server URL + 用户名** 保存多条独立记录；因此同一台服务器可使用不同账户，多台服务器也不会互相覆盖。

启动时如果存在至少一条已保存密码的记录，窗口进入简洁选择模式：显示服务器下拉框和用户名，隐藏密码标签与输入框。窗口默认选中最近使用的服务器，并回填服务器、用户名及（不可见的）已保存密码；用户仍需点击“连接”才会登录。选择“显示选项”后展开服务器、用户名和密码字段，并按当前选择项预填，便于核对或修改；“显示选项 / 隐藏选项”始终可切换。没有任何已保存密码时，窗口默认展开完整表单。

服务器下拉项和可编辑选择文本均只显示 Server URL。客户端在发送请求前验证 URL 必须为带 `http` 或 `https` 协议的完整地址；无效地址会显示错误提示，不会导致客户端异常退出。展开的密码框提供“查看 / 隐藏”按钮，用户可确认输入内容。

| 用户选择 | 本地保存内容 | 下次使用方式 |
|---|---|---|
| 勾选“记住此计算机和用户名”，不勾选保存密码 | Server URL、用户名 | 选择该项后会回填地址和用户名，仍需输入密码。 |
| 同时勾选“加密保存密码” | Server URL、用户名、密码 | 选择该项会回填凭据；用户点击“连接”后免密码登录。 |
| 不勾选“记住此计算机和用户名” | 不新增或更新本地记录 | 本次登录结束后不保留新输入。 |

密码不是普通配置：只有用户显式勾选后才会写入当前操作系统的安全存储（Windows DPAPI、macOS Keychain、Linux Secret Service），不会写入服务端、配置文件、日志或数据库。未保存密码的记录也不保存 RefreshToken，不能绕过密码输入。

旧版本的单条加密会话在首次读取时会迁移为一条新的连接记录。若保存的密码已失效，客户端会保留该服务器和用户名，但移除失效密码并提示用户重新输入；网络暂时不可达不会删除任何已保存连接。退出远程桌面只会注销当前会话，不会清除本地已保存连接，行为与 mstsc 的已保存凭据一致。
