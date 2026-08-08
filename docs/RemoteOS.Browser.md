# RemoteOS Browser 模块设计

> 内置网页浏览器：基于 `Avalonia.Controls.WebView` 的 `NativeWebView` 控件（平台原生引擎），书签与历史记录持久化到 Server 端（按用户隔离）；浏览器偏好（`BrowserSettings`）随 Workspace 持久化；可选的「本地端口映射」让客户端浏览器经 RemoteOS 鉴权访问**服务端 loopback** HTTP 服务。
>
> - 架构原则见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)
> - 项目当前状态见 [`RemoteOS.md`](./RemoteOS.md)（§6 内置应用 / §7 RemoteBrowser）
> - 桌面外壳与窗口管理见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)
> - 登录与身份认证见 [`RemoteOS.Login.md`](./RemoteOS.Login.md)（Browser 复用 `IAuthSession` JWT；本地端口映射用 JWT 换 HttpOnly cookie）
> - 通信协议契约见 [`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md)（§Browser DTO 与路由）
> - 服务端持久化见 [`RemoteOS.Storage.md`](./RemoteOS.Storage.md)（Bookmark / HistoryEntry 表 + Workspace `browser_settings` JSON 列）
> - 安全设计见 [`RemoteOS.Security.md`](./RemoteOS.Security.md)（本地端口映射仅 loopback，不做通用代理）

---

## 1. 定位

RemoteBrowser 是 RemoteOS 的内置网页浏览器。

- **架构归属**：§6.2 Remote Service Application —— 网页内容**在 Client 本地**由平台原生 WebView 引擎渲染（Win=WebView2 / macOS=WKWebView / Linux=WebKitGTK），Server **不代理**普通网页流量。
- **Server 持久化三类数据**：
  - 书签（Bookmark）/ 历史记录（HistoryEntry）：按用户隔离落 SQLite（`bookmarks` / `history_entries` 表）。
  - 浏览器偏好（`BrowserSettings`）：随 Workspace 持久化为 `browser_settings` JSON 列（`OwnsOne + ToJson`，与 `TerminalSettings` / `Preferences` 同模式）。
- **本地端口映射**（可选）：开启后客户端浏览器导航 `localhost:port` / `127.0.0.1:port` 时，请求改走 RemoteOS 鉴权通道转发到**服务端 loopback**——让用户在远端桌面里访问宿主 OS 上运行的 Web 服务（开发服务器 / 管理面板等）。**仅 loopback**（`localhost` + `127.0.0.1`），**不是通用代理**。
- **复用宿主 OS 权限**（project_memory 硬约束）：Server 端 SQLite 文件读写与 loopback 转发均以宿主 OS 进程身份执行，不另建 ACL。Browser REST 端点全 `RequireAuthorization`，按 JWT `sub` claim 取 userId 隔离数据。
- **不存储密码**：认证委托宿主 OS（已完成于登录模块），Browser 仅消费 `IAuthSession.Tokens.AccessToken`；本地端口映射的 HttpOnly cookie 不含宿主密码。

**实现范围**：导航（后退/前进/刷新/停止/主页/地址栏）+ 书签（加入/删除/侧边栏双击导航/清空全部）+ 历史（自动记录访问/侧边栏双击导航/单条删除/清空全部）+ 浏览器偏好持久化 + 本地端口映射（loopback → 服务端）。

---

## 2. 包与集成方式

### 2.1 NuGet 包

| 包 | 版本 | 用途 |
|----|------|------|
| `Avalonia.Controls.WebView` | 12.0.1 | Avalonia 12 兼容的 `NativeWebView` 控件（平台原生引擎：Win=WebView2 / macOS=WKWebView / Linux=WebKitGTK） |

- 中心化包管理：版本声明在 [`Directory.Packages.props`](../Directory.Packages.props)。
- **关键**：NuGet 包 id 是 `Avalonia.Controls.WebView`，**程序集名同包 id**（非 `Avalonia.WebView`）。XAML 命名空间：`xmlns:web="clr-namespace:Avalonia.Controls;assembly=Avalonia.Controls.WebView"`。控件在 `Avalonia.Controls` 命名空间但独立程序集，用 `<web:NativeWebView>` 而非 `<WebView>`。

### 2.2 嵌入而非替换 Shell

`BrowserMainView` 作为 `UserControl` 塞进 `RemoteWindow`，与 Notepad / Explorer / Terminal 同构：

```text
BrowserApp (RemoteApplicationBase)
    |
    AppContext.ShowWindow("RemoteBrowser", view)
    |
    WindowManager.Create → RemoteWindow
    |
    BrowserMainView (UserControl)
        ├── Top Toolbar (后退/前进/刷新/停止/主页/★书签/📑侧边栏/☐本地端口映射)
        ├── Address Bar (TextBox + 转到按钮)
        ├── Body Grid (侧边栏 | GridSplitter | NativeWebView)
        └── Status Bar (状态文本 + 加载进度)
```

未登录时 `BrowserApp.Activate` 弹提示窗（与 ExplorerApp 同模式），不崩溃。登录后 `BrowserApp` 调 `viewModel.LoadAsync()` 加载书签 + 历史 + 浏览器偏好（`BrowserSettings`）。

---

## 3. 服务端

### 3.1 领域模型（`Server.Domain/`）

- `Bookmark.cs`：`Id` / `UserId` / `Title` / `Url` / `CreatedAt` + `ToDto()`。同用户下 URL 唯一（应用层 UPSERT）。
- `HistoryEntry.cs`：`Id` / `UserId` / `Title` / `Url` / `VisitCount` / `FirstVisitedAt` / `LastVisitedAt` + `ToDto()`。同 URL 多次访问合并为一条（`VisitCount++`，`LastVisitedAt` 取最近）。
- `Workspace.cs`：`BrowserSettings` 属性（`BrowserSettingsDto`，默认 `Default`），与 `TerminalSettings` / `Preferences` 并列——浏览器偏好随 Workspace 持久化（`OwnsOne + ToJson`，见 §3.3）。

### 3.2 浏览器偏好（`BrowserSettingsDto`）

```text
BrowserSettingsDto (sealed record)
  LocalPortForwardingEnabled  bool   本地端口映射开关（默认 false）

  static Default { get; } = (LocalPortForwardingEnabled: false)
```

随 Workspace 持久化为 `browser_settings` JSON 列（`OwnsOne + ToJson`）。`BrowserSettings` 控制「本地端口映射」功能的启停——关闭时 `/api/v1/browser/local/*` 端点返回 403。

### 3.3 SQLite Schema（`RemoteOsDbContext.OnModelCreating`）

```text
workspaces（既有表）
  ...（既有列）
  terminal_settings   TEXT   (TerminalSettingsDto JSON)
  browser_settings    TEXT   (BrowserSettingsDto JSON，NULL→回退 Default)
  preferences         TEXT   (WorkspacePreferencesDto JSON)
```

`browser_settings` 作为 `OwnsOne + ToJson("browser_settings")` 挂在 `workspaces` 表，序列化为单列 JSON 文本（与 `terminal_settings` / `preferences` 同模式）。列允许 NULL——EF Core `OwnsOne+ToJson` 读取 NULL 时回退领域模型 `BrowserSettingsDto.Default`。

**建库兼容**：`EnsureCreated` 为新库建表时含 `browser_settings` 列；既有库（建库时无此列）由 `Program.cs` 启动时检测 `pragma_table_info('workspaces')` 并追加 `ALTER TABLE "workspaces" ADD COLUMN "browser_settings" TEXT NULL;` 增量补齐（与 `preferences` 列同模式）。

```text
bookmarks
  Id          TEXT PRIMARY KEY
  UserId      TEXT NOT NULL
  Title       TEXT
  Url         TEXT NOT NULL  (MaxLength=2048)
  CreatedAt   TEXT NOT NULL  (ISO8601)
  IX_bookmarks_UserId_Url         UNIQUE  (UPSERT 依据)
  IX_bookmarks_UserId                     (ListBookmarks 查询)

history_entries
  Id               TEXT PRIMARY KEY
  UserId           TEXT NOT NULL
  Title            TEXT
  Url              TEXT NOT NULL  (MaxLength=2048)
  VisitCount       INTEGER NOT NULL
  FirstVisitedAt   TEXT NOT NULL
  LastVisitedAt    TEXT NOT NULL
  IX_history_entries_UserId_Url            UNIQUE  (UPSERT 依据)
  IX_history_entries_UserId_LastVisitedAt        (ListHistory 排序查询)
```

**增量补齐**：`EnsureCreated` 不为已存在 db 追加新表/新列。`Program.cs` 启动时追加 `ALTER TABLE ... ADD COLUMN "browser_settings" TEXT NULL`（workspaces 列）+ `CREATE TABLE IF NOT EXISTS bookmarks / history_entries` SQL（与 EF Core 模型一致，索引对齐 OnModelCreating），兼容既有部署。

### 3.4 仓储（`Server.Storage/`）

`IBrowserRepository` 抽象，双实现：

- `InMemoryBrowserRepository`（Singleton，开发回退）：`ConcurrentDictionary<(userId, url), Bookmark>` + `ConcurrentDictionary<(userId, url), HistoryEntry>` 索引，配套 `*_byId` 字典。重启丢失（与 `InMemory*` 仓储一致）。
- `SqliteBrowserRepository`（Scoped，依赖 `RemoteOsDbContext`）：EF Core 实现。Find+Update 模式 UPSERT，`ExecuteDelete` 批量清空。

> 浏览器偏好（`BrowserSettings`）不经 `IBrowserRepository`——它挂在 `Workspace` 上，复用 `IWorkspaceRepository.FindByUserId` / `Update` 读写（与 `TerminalSettings` 同模式）。

### 3.5 本地端口映射（`Server.Browser/LocalPortForwarder.cs`）

把经 RemoteOS 鉴权的浏览器请求**复制**到服务端 loopback HTTP 服务并流式回传响应。**刻意不做通用代理**——只接受 `localhost` 与 `127.0.0.1`，只支持 http/https。

```text
LocalPortForwarder (Singleton, 依赖 IHttpClientFactory)
  ForwardAsync(context, host, scheme, port, path, ct)
    ├── 安全校验：IsLoopbackHost + IsSupportedScheme + port 1-65535（否则 400）
    ├── BuildTargetUri：拼 loopback 目标 URI（剔除 token query）
    ├── CreateRequest：复制方法/正文/请求头（剔除 Authorization/Cookie/Host/Connection 等跳跃头）
    │     └── 只转发被代理应用的 cookie，剔除 RemoteOS 鉴权 cookie（不暴露给 loopback 服务）
    ├── HttpClient.SendAsync(ResponseHeadersRead) → 流式 CopyToAsync 响应正文
    ├── CopyResponseHeaders：复制响应头（剔除 hop-by-hop）
    │     ├── Set-Cookie：重写 Path 为代理路径、剔除 Domain
    │     └── Location：绝对 loopback 重定向 → 代理 URI；相对 "/" 重定向 → 代理 URI
    └── 异常：HttpRequestException → 502；OperationCanceled → 浏览器断开，静默
```

**HttpClient 配置**（`Program.cs` 注册 `RemoteOS.LocalPortForwarding`）：

- `AllowAutoRedirect=false`——转发客户端不自动跟随重定向，由 `LocalPortForwarder` 校验并重写 `Location` 后回传给浏览器（防止一个用户的请求/会话状态泄漏到另一个用户的 loopback 服务）。
- `UseCookies=false`——转发客户端不带 cookie jar，每个请求独立（cookie 由 `LocalPortForwarder.CreateRequest` 显式构造）。
- `Timeout=5min`——长响应（如大文件下载）不被过早中断。

**JWT → HttpOnly cookie 交换**（`BrowserEndpoints` 本地端口映射端点内）：

首次 WebView 导航携带 query `remoteos_port_forwarding_token=<JWT>`，服务端把它换成 `RemoteOS.LocalPortForwarding.Auth` HttpOnly cookie（`SameSite=Strict` / `Secure`（HTTPS 时）/ `Path=/api/v1/browser/local` / `MaxAge=8h`），然后 302 重定向到去掉 token query 的同路径。后续子资源与表单请求自动带 cookie 鉴权——**JWT 不暴露给页面脚本**（HttpOnly）。

`Program.cs` 的 JwtBearer `Events.OnMessageReceived` 对 `/api/v1/browser/local` 路径从 query `remoteos_port_forwarding_token` **或** cookie `RemoteOS.LocalPortForwarding.Auth` 读取 token 注入 JwtBearer（兼容首次 query 与后续 cookie 两种携带方式）。

### 3.6 REST 端点（`Server.Endpoints/BrowserEndpoints.cs`）

11 个端点，全 `RequireAuthorization()`，按 JWT `sub` claim 取 userId 隔离数据。书签/历史错误统一 RFC 7807 `Results.Problem(..., type: "https://remoteos.app/problems/" + suffix)`；设置端点返回 `Results.Ok`/`Results.NotFound`，本地端口映射返回状态码 + 纯文本错误体。

| Method | Route | 用途 |
|--------|-------|------|
| GET | `/api/v1/browser/settings` | 读取当前 Workspace 的 `BrowserSettings` |
| PUT | `/api/v1/browser/settings` | 覆盖 `BrowserSettings`（返回归一化后 DTO） |
| * | `/api/v1/browser/local/{host}/{scheme}/{port:int}/{**path}` | 本地端口映射（GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS）；`BrowserSettings.LocalPortForwardingEnabled=false` 时 403 |
| GET | `/api/v1/browser/bookmarks` | 列举当前用户全部书签 |
| POST | `/api/v1/browser/bookmarks` | 新增（同 URL 则更新 Title） |
| DELETE | `/api/v1/browser/bookmarks/{id}` | 删除单条（仅当属于当前用户） |
| DELETE | `/api/v1/browser/bookmarks` | 清空当前用户全部书签 |
| GET | `/api/v1/browser/history?limit=` | 列举历史（按 LastVisitedAt 倒序，默认 100 上限 1000） |
| POST | `/api/v1/browser/history` | 记录一次访问（同 URL 则 VisitCount++） |
| DELETE | `/api/v1/browser/history/{id}` | 删除单条历史（仅当属于当前用户） |
| DELETE | `/api/v1/browser/history` | 清空当前用户全部历史 |

`Program.cs` 注册：`AddScoped<IBrowserRepository, SqliteBrowserRepository>()`（sqlite）/ `AddSingleton<IBrowserRepository, InMemoryBrowserRepository>()`（memory 回退）+ `AddSingleton<LocalPortForwarder>()` + `AddHttpClient(LocalPortForwarder.HttpClientName, ...)` + `app.MapBrowserEndpoints()`。

---

## 4. 协议契约（`Shared/RemoteOS.Protocol/Browser/`）

6 个文件，零 `PackageReference`，DTO 用 `sealed record` + `[property: JsonPropertyName]`：

| 文件 | 用途 |
|------|------|
| `BrowserSettingsDto.cs` | 浏览器偏好 DTO（`LocalPortForwardingEnabled` bool，`Default=false`）；随 Workspace 持久化 |
| `BookmarkDto.cs` | 书签 DTO（Id/UserId/Title/Url/CreatedAt） |
| `HistoryEntryDto.cs` | 历史 DTO（Id/UserId/Title/Url/VisitCount/FirstVisitedAt/LastVisitedAt） |
| `CreateBookmarkRequest.cs` | 新增书签请求体（Title/Url） |
| `CreateHistoryEntryRequest.cs` | 记录访问请求体（Title/Url） |
| `BrowserApiRoutes.cs` | 路由常量（路径含 `/api/v1` 前缀，Server 注册与 Client 拼接共用）：bookmarks/history 8 条 + `Settings`（GET/PUT）+ `LocalPortForwardingPrefix` / `LocalPortForwarding`（`{host}/{scheme}/{port:int}/{**path}`）+ `LocalPortForwardingAuthCookie` / `LocalPortForwardingTokenQuery` 常量 |

---

## 5. 客户端（`Client.Apps.Browser/`）

### 5.1 应用入口

`BrowserApp`（`RemoteApplicationBase`）：`Manifest`（Id=`remoteos.browser`，Icon=`🌐`）+ `Activate(AppContext)`。未登录弹 `TextBlock` 提示窗；登录则创建 `BrowserViewModel` + `BrowserMainView` + `context.ShowWindow`（bounds 1100x720）。

### 5.2 客户端 HTTP（`IBrowserClient` / `BrowserClient`）

- typed HttpClient（`Bootstrapper` 注册 `services.AddHttpClient<IBrowserClient, BrowserClient>()`）。
- **不 mutate `HttpClient.BaseAddress`**（避免共享实例并发竞态），每请求用 `IAuthSession.ServerUrl` 构造绝对 URI。
- `Authorization: Bearer {AccessToken}` 从 `IAuthSession.Tokens` 取；未登录抛 `InvalidOperationException`。
- 失败读 `ProblemDetails` 抛 `RemoteOsAuthException`（与 `RemoteOsClient` / `ExplorerClient` / `SettingsClient` 同源）。
- 路由常量共用 `BrowserApiRoutes`，禁止硬编码字符串。
- query 拼接手动 `Uri.EscapeDataString`（`QueryString.Create` 在 `Microsoft.AspNetCore.Http` 命名空间客户端不可用）。

**新增方法**（设置 + 本地端口映射）：

| 方法 | 用途 |
|------|------|
| `GetSettingsAsync()` | GET `/api/v1/browser/settings` → `BrowserSettingsDto` |
| `SaveSettingsAsync(settings)` | PUT `/api/v1/browser/settings` → 归一化后 `BrowserSettingsDto` |
| `CreateLocalPortForwardingUri(target)` | 把 loopback 目标 URI 改写为 RemoteOS 代理 URI（拼 `LocalPortForwardingPrefix/{host}/{scheme}/{port}/{path}` + 附 `remoteos_port_forwarding_token=<JWT>` query）；非 loopback 抛 `ArgumentException` |
| `TryGetLocalPortForwardingTarget(proxyUri)` | 反向：把代理 URI 解析回原始 loopback 目标 URI（用于地址栏显示）；非代理 URI 返回 null |

`CreateLocalPortForwardingUri` 与 `TryGetLocalPortForwardingTarget` 成对使用：导航前用前者把 `http://localhost:5000/foo` 改写为 `https://server/api/v1/browser/local/localhost/http/5000/foo?remoteos_port_forwarding_token=<JWT>` 交给 `NativeWebView`；`OnNavigationStarted` / `OnNavigationCompleted` 用后者把代理 URI 还原为 `http://localhost:5000/foo` 显示在地址栏与历史记录。

### 5.3 ViewModel（`BrowserViewModel`）

`CommunityToolkit.Mvvm`（`[ObservableProperty]` + `[RelayCommand]`）。状态：

- `WebViewSource`（Uri?，绑定 `NativeWebView.Source`）
- `AddressText` / `IsLoading` / `StatusText`
- `CanGoBack` / `CanGoForward`（由 View `UpdateNavigationState` 同步）
- `IsCurrentBookmarked`（★星标状态，`RefreshBookmarkStarAsync` 查 GET /bookmarks 判断）
- `ActiveSidebarTab`（Bookmarks / History）+ `IsSidebarVisible`
- `Bookmarks` / `History`（`ObservableCollection<...>`，侧边栏绑定）
- `IsLocalPortForwardingEnabled`（bool，双向绑定工具栏 CheckBox）+ `LocalPortForwardingStatus`（文案）

**初始化（`LoadAsync`）**：登录后由 `BrowserApp` 调用——并行加载书签 + 历史（上限 100）+ 浏览器偏好（`GetSettingsAsync`）。偏好加载失败时回退到 `_savedLocalPortForwardingEnabled`（保留本地值，后续可重试）。

**View ↔ VM 解耦**：VM 不持有 `NativeWebView` 引用，通过 `Action` 委托回调 View 实际方法：
- `ViewGoBackRequested` / `ViewGoForwardRequested` / `ViewRefreshRequested` / `ViewStopRequested`
- View code-behind `WireWebViewCommands` 注入：`() => WebView.GoBack()` 等
- 导航事件由 View 转发：`WebView_NavigationStarted` → `VM.OnNavigationStarted(url)`；`WebView_NavigationCompleted` → `VM.OnNavigationCompleted(url, isSuccess)`

**地址栏显示还原**：`ToDisplayUrl(url) = _client.TryGetLocalPortForwardingTarget(url) ?? url`——导航事件回传的代理 URI 还原为原始 loopback URI 显示在地址栏与历史记录，用户看到的始终是 `http://localhost:5000` 而非代理路径。

### 5.4 导航 → 历史记录流

```text
用户输地址 → NavigateCommand
    ├── NormalizeAddress：归一为绝对 Uri（无 scheme 的 "example.com" 补 https://；
    │   "localhost:9999" 补 http://；含空格当搜索查询交 bing）
    ├── 若 IsLocalPortForwardingEnabled && IsLoopbackTarget(uri)：
    │     uri = _client.CreateLocalPortForwardingUri(uri)   ← 改写为 RemoteOS 代理 URI + JWT query
    └── WebViewSource = uri（绑 NativeWebView.Source）
    ↓
NativeWebView.Source 绑定更新 → 触发 NavigationStarted
    ↓
View.WebView_NavigationStarted → VM.OnNavigationStarted(url)
    ├── displayUrl = TryGetLocalPortForwardingTarget(url) ?? url   ← 代理 URI 还原为 loopback 显示
    └── AddressText = displayUrl + IsLoading=true
    ↓
NativeWebView 加载完成 → NavigationCompleted
    ↓
View.WebView_NavigationCompleted → VM.OnNavigationCompleted(url, isSuccess=true)
    ├── displayUrl = TryGetLocalPortForwardingTarget(url) ?? url
    ├── fire-and-forget: RecordVisitAsync(displayUrl)
    │     └── POST /api/v1/browser/history (title=url, url=urlStr)
    │         └── 更新本地 History 列表（同 Id 替换 / 否则 Insert(0)，上限 100 条）
    └── fire-and-forget: RefreshBookmarkStarAsync(displayUrl)
          └── GET /api/v1/browser/bookmarks 查找 Url==url
              └── IsCurrentBookmarked = (找到 != null)
```

### 5.5 本地端口映射流（开启时）

```text
用户在工具栏勾选「本地端口映射」CheckBox
    ↓
SaveLocalPortForwardingCommand
    └── PUT /api/v1/browser/settings (body: BrowserSettingsDto(IsLocalPortForwardingEnabled))
        ├── 成功：IsLocalPortForwardingEnabled = saved.LocalPortForwardingEnabled
        │         LocalPortForwardingStatus = "已开启（localhost 请求将访问远程计算机）"
        └── 失败：回退 _savedLocalPortForwardingEnabled，StatusText 显示错误

用户导航 http://localhost:5000/foo
    ↓
NavigateCommand 检测 IsLocalPortForwardingEnabled && IsLoopbackTarget
    └── uri = CreateLocalPortForwardingUri(http://localhost:5000/foo)
          = https://server/api/v1/browser/local/localhost/http/5000/foo
            ?remoteos_port_forwarding_token=<JWT>
    ↓
NativeWebView 加载代理 URI（携带 JWT query）
    ↓
Server: JwtBearer OnMessageReceived 从 query 读 token → 鉴权通过
    ↓
BrowserEndpoints 本地端口映射端点：
    ├── 检测 BrowserSettings.LocalPortForwardingEnabled（false → 403）
    ├── 检测 query remoteos_port_forwarding_token 存在 → 换 HttpOnly cookie + 302 重定向去 token
    └── LocalPortForwarder.ForwardAsync → 转发到 127.0.0.1:5000/foo，流式回传响应
    ↓
后续子资源/表单请求自动带 HttpOnly cookie 鉴权（JWT 不暴露给脚本）
```

### 5.6 转换器（`Converters/BrowserConverters.cs`）

- `BookmarkStarConverter`：`IsCurrentBookmarked`（bool）→ "★" / "☆"
- `SidebarTabVisibilityConverter`：`ActiveSidebarTab` + `ConverterParameter` → 是否显示对应 ListBox
- `SidebarTabBgConverter`：`ActiveSidebarTab` + `ConverterParameter` → 标签页按钮背景色（激活/非激活）

---

## 6. 关键技术坑

1. **XAML 命名空间**：`xmlns:web="clr-namespace:Avalonia.Controls;assembly=Avalonia.Controls.WebView"`（控件在 `Avalonia.Controls` 命名空间但独立程序集）。用 `<web:NativeWebView>` 而非 `<WebView>`。
2. **事件参数类型**：`NativeWebView.NavigationStarted`/`NavigationCompleted` 事件参数类型为 `Avalonia.Controls.WebViewNavigationStartingEventArgs` / `WebViewNavigationCompletedEventArgs`。XAML 解析器报 AVLN3000 时，在 code-behind 显式 using 全名即可。
3. **VM 不持有 WebView 引用**：`NativeWebView` 的 `CanGoBack`/`CanGoForward`/`GoBack()`/`GoForward()`/`Refresh()`/`Stop()` 是实例方法，必须由 View code-behind 调用。用 `Action` 委托（`ViewGoBackRequested` 等）由 View 在 `WireWebViewCommands` 注入实际方法。
4. **未授权返回 401**：所有 `/api/v1/browser/*` 端点 `RequireAuthorization` 生效。端点已注册（404 → 401）。客户端 `BrowserClient.RequireSession` 检查 `IAuthSession.State == Authenticated`。
5. **SQLite 增量补齐**：`EnsureCreated` 不为已存在 db 追加新表/新列。`Program.cs` 启动时追加 `ALTER TABLE "workspaces" ADD COLUMN "browser_settings" TEXT NULL`（workspaces 列）+ `CREATE TABLE IF NOT EXISTS bookmarks / history_entries` SQL 兼容既有部署（与 EF Core 模型一致，索引对齐 OnModelCreating）。
6. **history fire-and-forget**：`OnNavigationCompleted` 内 `_ = RecordVisitAsync(url)` 不 await，失败只更新 `StatusText` 不阻塞 UI。本地 `History` 列表上限 100 条（避免无限增长）。
7. **本地端口映射仅 loopback**：`LocalPortForwarder` 只接受 `localhost` 与 `127.0.0.1` + http/https，`host` 是路由值且服务端校验——**刻意不做通用代理**（防止 SSRF）。客户端 `CreateLocalPortForwardingUri` / `TryGetLocalPortForwardingTarget` 同样校验 loopback。
8. **JWT → HttpOnly cookie 交换**：WebView 首次导航带 query token，服务端换 `RemoteOS.LocalPortForwarding.Auth` HttpOnly cookie（`Path=/api/v1/browser/local` / `SameSite=Strict` / `MaxAge=8h`）。**禁止**把 JWT 直接留在 query 里供子资源用——会暴露给页面脚本。JwtBearer `OnMessageReceived` 对 `/api/v1/browser/local` 路径同时支持 query token 与 cookie 两种携带方式。
9. **转发 HttpClient 禁跟随重定向 / 禁 cookie jar**：`AllowAutoRedirect=false` + `UseCookies=false`——重定向由 `LocalPortForwarder` 校验并重写 `Location` 后回传（防止跨用户 loopback 服务会话状态泄漏）；cookie 由 `CreateRequest` 显式构造（剔除 RemoteOS 鉴权 cookie，只转发被代理应用的 cookie）。
10. **`Set-Cookie` / `Location` 重写**：被代理应用的 `Set-Cookie` 需重写 `Path` 为代理路径、剔除 `Domain`（否则浏览器丢弃跨域 cookie）；`Location` 重定向（绝对 loopback 或相对 `/`）需改写为代理 URI，否则浏览器跳出代理通道。
11. **地址栏还原**：`OnNavigationStarted`/`OnNavigationCompleted` 收到的是代理 URI，必须用 `TryGetLocalPortForwardingTarget` 还原为原始 loopback URI 显示——否则用户看到的是冗长的代理路径，历史记录也会存入代理 URI 而非用户输入的地址。
12. **`SaveSettingsAsync` 失败回退**：`SaveLocalPortForwardingCommand` 失败时把 `IsLocalPortForwardingEnabled` 回退到 `_savedLocalPortForwardingEnabled`（上次成功保存的值），避免 CheckBox 状态与服务端不一致。

---

## 7. 后续演进

- **页面 Title 提取**：当前用 URL 作 title。后续可订阅 `NativeWebView.DocumentTitleChanged`（若包支持）取真实 `<title>`。
- **多标签页**：当前单 WebView。后续引入 TabStrip + 多 NativeWebView 实例。
- **Cookie / 下载管理**：当前未实现。后续按需接入 `NativeWebView` 的 CookieManager / Download 事件。
- **地址栏搜索建议**：当前仅支持 URL/搜索词直接导航。后续接入搜索引擎建议 API。
- **书签分类/文件夹**：当前扁平列表。后续引入 `BookmarkFolder` 表与 `ParentId` 层级。
- **历史搜索/时间范围筛选**：当前仅按 LastVisitedAt 倒序。后续加 `?q=` / `?from=&to=` query 参数。
- **本地端口映射范围扩展**：当前仅 `localhost` / `127.0.0.1`。后续按需支持 Unix domain socket 或受限的内网地址段（仍需服务端校验，不做开放代理）。
- **端口映射会话过期续期**：当前 cookie `MaxAge=8h`，过期需重新导航触发 token 交换。后续可接续期端点。

---

## 8. AI Agent Rules

> 实现与维护本模块时必须遵守的规则。

1. **网页渲染在 Client，持久化在 Server**：`NativeWebView` 用平台原生引擎渲染网页内容（走客户端网络），Server **不代理**普通网页流量。Server 持久化书签 / 历史 / 浏览器偏好（`BrowserSettings`）。禁止引入 Server 端 HTML 解析或通用 HTTP 代理。
2. **本地端口映射仅 loopback**：`LocalPortForwarder` 只接受 `localhost` 与 `127.0.0.1` + http/https，`host` 是路由值且服务端校验。**禁止**放开为通用代理（SSRF 风险）。功能受 `BrowserSettings.LocalPortForwardingEnabled` 门控（false → 403）。
3. **复用 `IAuthSession` JWT**：`IBrowserClient` 不持有独立凭据；未登录调 `BrowserApp.Activate` 弹提示窗，不崩溃。`BrowserClient.RequireSession` 检查 `State == Authenticated`。
4. **不 mutate `HttpClient.BaseAddress`**：每请求用绝对 URI（避免共享 typed HttpClient 实例并发竞态），与 `ExplorerClient` / `SettingsClient` / `TaskManagerClient` 同模式。
5. **路由常量共用 `BrowserApiRoutes`**：Server 注册与 Client 拼接 URL 必须用同一常量，禁止硬编码字符串。
6. **DTO 用 `sealed record` + `[property: JsonPropertyName]`**（Protocol 约定），线协议用 `System.Text.Json`（`RemoteOsJsonOptions.Default`）。
7. **按用户隔离**：所有仓储方法首参 `Guid userId`，从 JWT `sub` claim 取（`GetUserId(principal)`）。禁止跨用户读写。
8. **UPSERT 语义**：bookmark 同 URL 重复 → 更新 Title（不重置 CreatedAt）；history 同 URL 重复 → `VisitCount++` + 更新 `LastVisitedAt`（不重置 `FirstVisitedAt`）。靠 `(userId, url)` 唯一索引保证。
9. **VM 不持有 WebView 引用**：导航方法（GoBack/GoForward/Refresh/Stop）通过 `Action` 委托由 View code-behind 注入实际 `NativeWebView` 方法。VM 仅持有 `WebViewSource`（Uri?，双向绑定）。
10. **history 记录 fire-and-forget**：`OnNavigationCompleted` 内 `_ = RecordVisitAsync(url)` 不 await，失败不阻塞 UI。本地 `History` 列表上限 100 条。
11. **SQLite 增量补齐**：新增表/列时必须在 `Program.cs` 追加 `CREATE TABLE IF NOT EXISTS` / `ALTER TABLE ... ADD COLUMN ... TEXT NULL` SQL（与 EF Core 模型一致，索引对齐 OnModelCreating），兼容既有部署（`EnsureCreated` 不为已存在 db 追加新表/新列）。
12. **`BrowserSettings` 随 Workspace 持久化**：`OwnsOne + ToJson("browser_settings")` 单列 JSON（与 `TerminalSettings` / `Preferences` 同模式）。新增偏好字段扩 `BrowserSettingsDto`（JSON 列内增字段，无需改 schema）；领域模型 `Workspace` 加属性 + DbContext `OwnsOne+ToJson`。禁止为浏览器偏好新建独立表。
13. **本地端口映射 JWT → HttpOnly cookie**：首次导航带 query `remoteos_port_forwarding_token`，服务端换 `RemoteOS.LocalPortForwarding.Auth` HttpOnly cookie（`Path=/api/v1/browser/local` / `SameSite=Strict` / `MaxAge=8h`）。**禁止**把 JWT 留在 query 供子资源用（暴露给脚本）。JwtBearer `OnMessageReceived` 对 `/api/v1/browser/local` 同时支持 query token 与 cookie。
14. **转发 HttpClient 禁跟随重定向 / 禁 cookie jar**：`AllowAutoRedirect=false` + `UseCookies=false`。重定向由 `LocalPortForwarder` 校验并重写 `Location`；cookie 由 `CreateRequest` 显式构造（剔除 RemoteOS 鉴权 cookie，只转发被代理应用的 cookie）。`Set-Cookie` 重写 `Path` 为代理路径、剔除 `Domain`。
15. **地址栏还原**：导航事件回传的代理 URI 必须用 `TryGetLocalPortForwardingTarget` 还原为原始 loopback URI 显示在地址栏与历史记录。
16. **编译验证**：`dotnet build RemoteOS.sln -c Debug` 必须 0 错误（NU1903 Microsoft.OpenApi / SQLitePCLRaw.lib.e_sqlite3 与 CS0169 TerminalSession._disposed 为既有警告，非本模块引入）。
