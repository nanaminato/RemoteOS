# RemoteOS Browser 模块设计

> 内置网页浏览器：基于 `Avalonia.Controls.WebView` 的 `NativeWebView` 控件（平台原生引擎），书签与历史记录持久化到 Server 端（按用户隔离）。
>
> - 架构原则见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)
> - 项目当前状态见 [`RemoteOS.md`](./RemoteOS.md)（§6 内置应用 / §7 RemoteBrowser）
> - 桌面外壳与窗口管理见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)
> - 登录与身份认证见 [`RemoteOS.Login.md`](./RemoteOS.Login.md)（Browser 复用 `IAuthSession` JWT）
> - 通信协议契约见 [`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md)（§Browser DTO 与路由）
> - 服务端持久化见 [`RemoteOS.Storage.md`](./RemoteOS.Storage.md)（Bookmark / HistoryEntry 表）

---

## 1. 定位

RemoteBrowser 是 RemoteOS 的内置网页浏览器。

- **架构归属**：§6.2 Remote Service Application —— 网页内容**在 Client 本地**由平台原生 WebView 引擎渲染（Win=WebView2 / macOS=WKWebView / Linux=WebKitGTK），Server **不代理**网页流量。
- **Server 仅持久化**：书签（Bookmark）与历史记录（HistoryEntry）按用户隔离落 SQLite（与 User/Workspace/Device 同库，见 `RemoteOS.Storage.md`）。
- **复用宿主 OS 权限**（project_memory 硬约束）：Server 端 SQLite 文件读写以宿主 OS 进程身份执行，不另建 ACL。Browser REST 端点全 `RequireAuthorization`，按 JWT `sub` claim 取 userId 隔离数据。
- **不存储密码**：认证委托宿主 OS（已完成于登录模块），Browser 仅消费 `IAuthSession.Tokens.AccessToken`。

**MVP 范围**：导航（后退/前进/刷新/停止/主页/地址栏）+ 书签（加入/删除/侧边栏双击导航/清空全部）+ 历史（自动记录访问/侧边栏双击导航/单条删除/清空全部）。

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
        ├── Top Toolbar (后退/前进/刷新/停止/主页/★书签/📑侧边栏)
        ├── Address Bar (TextBox + 转到按钮)
        ├── Body Grid (侧边栏 | GridSplitter | NativeWebView)
        └── Status Bar (状态文本 + 加载进度)
```

未登录时 `BrowserApp.Activate` 弹提示窗（与 ExplorerApp 同模式），不崩溃。

---

## 3. 服务端

### 3.1 领域模型（`Server.Domain/`）

- `Bookmark.cs`：`Id` / `UserId` / `Title` / `Url` / `CreatedAt` + `ToDto()`。同用户下 URL 唯一（应用层 UPSERT）。
- `HistoryEntry.cs`：`Id` / `UserId` / `Title` / `Url` / `VisitCount` / `FirstVisitedAt` / `LastVisitedAt` + `ToDto()`。同 URL 多次访问合并为一条（`VisitCount++`，`LastVisitedAt` 取最近）。

### 3.2 仓储（`Server.Storage/`）

`IBrowserRepository` 抽象，双实现：

- `InMemoryBrowserRepository`（Singleton，开发回退）：`ConcurrentDictionary<(userId, url), Bookmark>` + `ConcurrentDictionary<(userId, url), HistoryEntry>` 索引，配套 `*_byId` 字典。重启丢失（与 `InMemory*` 仓储一致）。
- `SqliteBrowserRepository`（Scoped，依赖 `RemoteOsDbContext`）：EF Core 实现。Find+Update 模式 UPSERT，`ExecuteDelete` 批量清空。

### 3.3 SQLite Schema（`RemoteOsDbContext.OnModelCreating`）

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

**增量补齐**：`EnsureCreated` 不为已存在 db 追加新表。`Program.cs` 启动时追加 `CREATE TABLE IF NOT EXISTS bookmarks / history_entries` SQL（与 EF Core 模型一致，索引对齐 OnModelCreating），兼容既有部署。

### 3.4 REST 端点（`Server.Endpoints/BrowserEndpoints.cs`）

8 个端点，全 `RequireAuthorization()`，按 JWT `sub` claim 取 userId 隔离数据。错误统一 RFC 7807 `Results.Problem(..., type: "https://remoteos.app/problems/" + suffix)`。

| Method | Route | 用途 |
|--------|-------|------|
| GET | `/api/v1/browser/bookmarks` | 列举当前用户全部书签 |
| POST | `/api/v1/browser/bookmarks` | 新增（同 URL 则更新 Title） |
| DELETE | `/api/v1/browser/bookmarks/{id}` | 删除单条（仅当属于当前用户） |
| DELETE | `/api/v1/browser/bookmarks` | 清空当前用户全部书签 |
| GET | `/api/v1/browser/history?limit=` | 列举历史（按 LastVisitedAt 倒序，默认 100 上限 1000） |
| POST | `/api/v1/browser/history` | 记录一次访问（同 URL 则 VisitCount++） |
| DELETE | `/api/v1/browser/history/{id}` | 删除单条历史（仅当属于当前用户） |
| DELETE | `/api/v1/browser/history` | 清空当前用户全部历史 |

`Program.cs` 注册：`AddScoped<IBrowserRepository, SqliteBrowserRepository>()`（sqlite）/ `AddSingleton<IBrowserRepository, InMemoryBrowserRepository>()`（memory 回退）+ `app.MapBrowserEndpoints()`。

---

## 4. 协议契约（`Shared/RemoteOS.Protocol/Browser/`）

5 个文件，零 `PackageReference`，DTO 用 `sealed record` + `[property: JsonPropertyName]`：

| 文件 | 用途 |
|------|------|
| `BookmarkDto.cs` | 书签 DTO（Id/UserId/Title/Url/CreatedAt） |
| `HistoryEntryDto.cs` | 历史 DTO（Id/UserId/Title/Url/VisitCount/FirstVisitedAt/LastVisitedAt） |
| `CreateBookmarkRequest.cs` | 新增书签请求体（Title/Url） |
| `CreateHistoryEntryRequest.cs` | 记录访问请求体（Title/Url） |
| `BrowserApiRoutes.cs` | 路由常量（路径含 `/api/v1` 前缀，Server 注册与 Client 拼接共用） |

---

## 5. 客户端（`Client.Apps.Browser/`）

### 5.1 应用入口

`BrowserApp`（`RemoteApplicationBase`）：`Manifest`（Id=`remoteos.browser`，Icon=`🌐`）+ `Activate(AppContext)`。未登录弹 `TextBlock` 提示窗；登录则创建 `BrowserViewModel` + `BrowserMainView` + `context.ShowWindow`（bounds 1100x720）。

### 5.2 客户端 HTTP（`IBrowserClient` / `BrowserClient`）

- typed HttpClient（`Bootstrapper` 注册 `services.AddHttpClient<IBrowserClient, BrowserClient>()`）。
- **不 mutate `HttpClient.BaseAddress`**（避免共享实例并发竞态），每请求用 `IAuthSession.ServerUrl` 构造绝对 URI。
- `Authorization: Bearer {AccessToken}` 从 `IAuthSession.Tokens` 取；未登录抛 `InvalidOperationException`。
- 失败读 `ProblemDetails` 抛 `RemoteOsAuthException`（与 `RemoteOsClient` / `ExplorerClient` 同源）。
- 路由常量共用 `BrowserApiRoutes`，禁止硬编码字符串。
- query 拼接手动 `Uri.EscapeDataString`（`QueryString.Create` 在 `Microsoft.AspNetCore.Http` 命名空间客户端不可用）。

### 5.3 ViewModel（`BrowserViewModel`）

`CommunityToolkit.Mvvm`（`[ObservableProperty]` + `[RelayCommand]`）。状态：

- `WebViewSource`（Uri?，绑定 `NativeWebView.Source`）
- `AddressText` / `IsLoading` / `StatusText`
- `CanGoBack` / `CanGoForward`（由 View `UpdateNavigationState` 同步）
- `IsCurrentBookmarked`（★星标状态，`RefreshBookmarkStarAsync` 查 GET /bookmarks 判断）
- `ActiveSidebarTab`（Bookmarks / History）+ `IsSidebarVisible`
- `Bookmarks` / `History`（`ObservableCollection<...>`，侧边栏绑定）

**View ↔ VM 解耦**：VM 不持有 `NativeWebView` 引用，通过 `Action` 委托回调 View 实际方法：
- `ViewGoBackRequested` / `ViewGoForwardRequested` / `ViewRefreshRequested` / `ViewStopRequested`
- View code-behind `WireWebViewCommands` 注入：`() => WebView.GoBack()` 等
- 导航事件由 View 转发：`WebView_NavigationStarted` → `VM.OnNavigationStarted(url)`；`WebView_NavigationCompleted` → `VM.OnNavigationCompleted(url, isSuccess)`

### 5.4 导航 → 历史记录流

```text
用户输地址 → NavigateCommand 设 WebViewSource
    ↓
NativeWebView.Source 绑定更新 → 触发 NavigationStarted
    ↓
View.WebView_NavigationStarted → VM.OnNavigationStarted(url)
    （更新 AddressText + IsLoading=true）
    ↓
NativeWebView 加载完成 → NavigationCompleted
    ↓
View.WebView_NavigationCompleted → VM.OnNavigationCompleted(url, isSuccess=true)
    ├── fire-and-forget: RecordVisitAsync(url)
    │     └── POST /api/v1/browser/history (title=url, url=urlStr)
    │         └── 更新本地 History 列表（同 Id 替换 / 否则 Insert(0)，上限 100 条）
    └── fire-and-forget: RefreshBookmarkStarAsync(url)
          └── GET /api/v1/browser/bookmarks 查找 Url==url
              └── IsCurrentBookmarked = (找到 != null)
```

### 5.5 转换器（`Converters/BrowserConverters.cs`）

- `BookmarkStarConverter`：`IsCurrentBookmarked`（bool）→ "★" / "☆"
- `SidebarTabVisibilityConverter`：`ActiveSidebarTab` + `ConverterParameter` → 是否显示对应 ListBox
- `SidebarTabBgConverter`：`ActiveSidebarTab` + `ConverterParameter` → 标签页按钮背景色（激活/非激活）

---

## 6. 关键技术坑

1. **XAML 命名空间**：`xmlns:web="clr-namespace:Avalonia.Controls;assembly=Avalonia.Controls.WebView"`（控件在 `Avalonia.Controls` 命名空间但独立程序集）。用 `<web:NativeWebView>` 而非 `<WebView>`。
2. **事件参数类型**：`NativeWebView.NavigationStarted`/`NavigationCompleted` 事件参数类型为 `Avalonia.Controls.WebViewNavigationStartingEventArgs` / `WebViewNavigationCompletedEventArgs`。XAML 解析器报 AVLN3000 时，在 code-behind 显式 using 全名即可。
3. **VM 不持有 WebView 引用**：`NativeWebView` 的 `CanGoBack`/`CanGoForward`/`GoBack()`/`GoForward()`/`Refresh()`/`Stop()` 是实例方法，必须由 View code-behind 调用。用 `Action` 委托（`ViewGoBackRequested` 等）由 View 在 `WireWebViewCommands` 注入实际方法。
4. **未授权返回 401**：所有 `/api/v1/browser/*` 端点 `RequireAuthorization` 生效。端点已注册（404 → 401）。客户端 `BrowserClient.RequireSession` 检查 `IAuthSession.State == Authenticated`。
5. **SQLite 增量补齐**：`EnsureCreated` 不为已存在 db 追加新表。`Program.cs` 启动时追加 `CREATE TABLE IF NOT EXISTS bookmarks / history_entries` SQL 兼容既有部署（与 EF Core 模型一致，索引对齐 OnModelCreating）。
6. **history fire-and-forget**：`OnNavigationCompleted` 内 `_ = RecordVisitAsync(url)` 不 await，失败只更新 `StatusText` 不阻塞 UI。本地 `History` 列表上限 100 条（避免无限增长）。

---

## 7. 后续演进

- **页面 Title 提取**：MVP 用 URL 作 title。后续可订阅 `NativeWebView.DocumentTitleChanged`（若包支持）取真实 `<title>`。
- **多标签页**：MVP 单 WebView。后续引入 TabStrip + 多 NativeWebView 实例。
- **Cookie / 下载管理**：MVP 未实现。后续按需接入 `NativeWebView` 的 CookieManager / Download 事件。
- **地址栏搜索建议**：MVP 仅支持 URL/搜索词直接导航。后续接入搜索引擎建议 API。
- **书签分类/文件夹**：MVP 扁平列表。后续引入 `BookmarkFolder` 表与 `ParentId` 层级。
- **历史搜索/时间范围筛选**：MVP 仅按 LastVisitedAt 倒序。后续加 `?q=` / `?from=&to=` query 参数。

---

## 8. AI Agent Rules

> 实现与维护本模块时必须遵守的规则。

1. **网页渲染在 Client，持久化在 Server**：`NativeWebView` 用平台原生引擎渲染网页内容（走客户端网络），Server **不代理**网页流量。Server 仅持久化书签与历史记录（按用户隔离）。禁止引入 Server 端 HTTP 代理 / HTML 解析。
2. **复用 `IAuthSession` JWT**：`IBrowserClient` 不持有独立凭据；未登录调 `BrowserApp.Activate` 弹提示窗，不崩溃。`BrowserClient.RequireSession` 检查 `State == Authenticated`。
3. **错误统一 RFC 7807**：Server `Results.Problem(..., type: "https://remoteos.app/problems/" + suffix)`；Client `BrowserClient` 解析 `ProblemDetails` 抛 `RemoteOsAuthException`，VM catch 后写 `StatusText`。
4. **路由常量共用 `BrowserApiRoutes`**：Server 注册与 Client 拼接 URL 必须用同一常量，禁止硬编码字符串。
5. **DTO 用 `sealed record` + `[property: JsonPropertyName]`**（Protocol 约定），线协议用 `System.Text.Json`（`RemoteOsJsonOptions.Default`）。
6. **按用户隔离**：所有仓储方法首参 `Guid userId`，从 JWT `sub` claim 取（`GetUserId(principal)`）。禁止跨用户读写。
7. **UPSERT 语义**：bookmark 同 URL 重复 → 更新 Title（不重置 CreatedAt）；history 同 URL 重复 → `VisitCount++` + 更新 `LastVisitedAt`（不重置 `FirstVisitedAt`）。靠 `(userId, url)` 唯一索引保证。
8. **VM 不持有 WebView 引用**：导航方法（GoBack/GoForward/Refresh/Stop）通过 `Action` 委托由 View code-behind 注入实际 `NativeWebView` 方法。VM 仅持有 `WebViewSource`（Uri?，双向绑定）。
9. **history 记录 fire-and-forget**：`OnNavigationCompleted` 内 `_ = RecordVisitAsync(url)` 不 await，失败不阻塞 UI。本地 `History` 列表上限 100 条。
10. **SQLite 增量补齐**：新增表时必须在 `Program.cs` 追加 `CREATE TABLE IF NOT EXISTS` SQL（与 EF Core 模型一致，索引对齐 OnModelCreating），兼容既有部署（`EnsureCreated` 不为已存在 db 追加新表）。
11. **编译验证**：`dotnet build RemoteOS.sln -c Debug` 必须 0 错误（NU1903 Microsoft.OpenApi / SQLitePCLRaw.lib.e_sqlite3 与 CS0169 TerminalSession._disposed 为既有警告，非本模块引入）。
