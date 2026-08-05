# RemoteOS Storage 持久化设计文档

> 本文档定义 RemoteOS.Server 的持久化存储方案：技术选型、持久化范围、表结构、仓储层、建库策略、配置项，以及与登录流程的交互。
>
> 本文档针对「配置 + 身份」这一组持久实体落地（User / Workspace / Device，含终端外观配置 TerminalSettings），让终端配置等服务端状态跨重启保留。
>
> - 用户/Workspace 模型见 [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)
> - 登录与身份见 [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md) / [`RemoteOS.Login.md`](./RemoteOS.Login.md)
> - 服务端整体见 [`RemoteOS.md`](./RemoteOS.md) §4.9

---

## 1. 背景

`RemoteOS.Server` 此前所有仓储均为内存实现（`InMemory*Repository`，Singleton，`ConcurrentDictionary`），重启即丢。其中**终端外观配置**（`TerminalSettingsDto`：FontFamily / FontSize / ColorScheme / Background/Foreground/CursorColor）作为 [Workspace](../RemoteOS.Server/Domain/Workspace.cs) 的属性存在内存中，经 `GET/PUT /api/v1/workspaces/{id}/terminal-settings` 读写——用户改完配置重启服务就丢失。

而 Workspace 在 [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md) §22/§23 中被明确定义为 **「One Persistent Workspace」**。本次引入 SQLite 持久化层，先把「配置 + 身份」这一组持久实体落地，让终端配置跨重启保留，并为后续 Storage / 同步能力奠基。

---

## 2. 设计目标

- **配置不丢**：终端外观配置（TerminalSettings）随 Workspace 持久化，重启后恢复。
- **身份连续**：User / Device 记录跨重启保留，登录命中既有记录而非每次重建（保留 CreatedAt / LastLoginAt 历史）。
- **零业务侵入**：保留现有 `IUserRepository` / `IWorkspaceRepository` / `IDeviceRepository` 接口，仅新增 EF 实现 + DI 切换；端点与领域模型不改。
- **可回退**：开发期可通过配置切回内存仓储（`Provider=memory`）。
- **可演进**：表结构由 EF Core 模型配置定义，未来可平滑切换到 Migrations。

---

## 3. 技术选型

**EF Core + SQLite Provider**（`Microsoft.EntityFrameworkCore.Sqlite` 10.0.10）。

| 维度 | 选择 | 理由 |
|------|------|------|
| 数据库 | SQLite | 单文件本地库，零运维，契合单服务器/小团队场景；跨平台（Ubuntu / Windows Server） |
| 数据访问 | EF Core | 规范模型配置、Migrations 可演进、强类型 DbSet；与 .NET 生态主流一致 |
| 配置列存储 | `OwnsOne + ToJson` | TerminalSettings（6 字段 record）序列化为单列 JSON 文本，配置可演进——新增外观字段无需改 schema |
| 枚举存储 | `HasConversion<string>()` | 与线协议（camelCase 字符串）对齐，可读性好 |

> 领域模型 `User` / `Workspace` / `Device` 为公开 setter + 无参构造的 plain class，EF 友好，无需改造。`TerminalSettingsDto` 为 sealed record，由 EF 的 JSON 列映射经 System.Text.Json 序列化/反序列化（record 主构造支持）。

### 包（中心化包管理）

[`Directory.Packages.props`](../Directory.Packages.props) 新增：
- `Microsoft.EntityFrameworkCore.Sqlite` 10.0.10（含 `Microsoft.Data.Sqlite`）
- `Microsoft.EntityFrameworkCore.Design` 10.0.10（Design-time，`PrivateAssets=all`）

---

## 4. 持久化范围（关键决策）

并非所有运行时状态都应持久化。按实体语义划分：

| 实体 | 是否持久化 | 理由 |
|------|-----------|------|
| **User** | ✅ SQLite | 登录 `FindByUsername` 命中后复用；若不持久化，重启后 User.Id 变化 → `FindByUserId` 找不到旧 Workspace → TerminalSettings 成孤儿丢失。**必须与 Workspace 配套**。 |
| **Workspace**（含 TerminalSettings / BrowserSettings / Preferences） | ✅ SQLite | 用户核心诉求；文档定义为 Persistent。三组配置均以 JSON 列随 Workspace 持久（`OwnsOne + ToJson`）。TerminalSettings = 终端外观；BrowserSettings = 浏览器配置；Preferences = 设置中心偏好（壁纸/主题/时间格式/语言/区域/默认程序，见 [`RemoteOS.Settings.md`](./RemoteOS.Settings.md)）。 |
| **Device** | ✅ SQLite | 设备登记历史，与 User/Workspace 同属「持久实体」，保持一致。 |
| Session | ❌ 内存 | 「连接关系」是运行时状态（Created→Active→Disconnected→Expired），重启后旧 Session 本就应失效，用户重新登录即可。持久化反而引入状态不一致。 |
| AuthSessionStore（refresh token） | ❌ 内存 | 安全令牌重启失效 = 强制重新登录，符合安全语义（与 mstsc 默认不保存凭据一致）。 |
| TerminalSessionManager（PTY + 环形缓冲） | ❌ 内存 | PTY 是活进程，无法序列化；重启后用户重连新建 PTY + 回放缓冲（缓冲内存丢失为已知行为，见 [`RemoteOS.Terminal.md`](./RemoteOS.Terminal.md)）。 |

> 结论：本次持久化 **User + Workspace + Device**。Session / refresh token / PTY 维持内存，符合各自语义。

---

## 5. 表结构

数据库文件默认 `{ContentRoot}/data/remoteos.db`（见 §7）。表名小写复数，由 `RemoteOsDbContext.OnModelCreating` 定义。

### 5.1 users

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| Username | TEXT | NOT NULL，≤128 |
| Platform | TEXT | NOT NULL，≤32（枚举字符串） |
| PlatformIdentity | TEXT | ≤256 |
| CreatedAt | TEXT | NOT NULL（ISO 8601） |
| LastLoginAt | TEXT | NULL |

- 唯一索引：`(Username, Platform)`——对应 `InMemoryUserRepository._byName`

### 5.2 workspaces

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| UserId | TEXT | NOT NULL |
| Name | TEXT | NOT NULL，≤256 |
| State | TEXT | NOT NULL，≤32（枚举字符串） |
| CreatedAt | TEXT | NOT NULL |
| terminal_settings | TEXT | NOT NULL（JSON，TerminalSettingsDto 序列化） |
| browser_settings | TEXT | NULL（JSON，BrowserSettingsDto 序列化；既有库增量补齐） |
| preferences | TEXT | NULL（JSON，WorkspacePreferencesDto 序列化；既有库增量补齐） |
| ControllerDeviceId | TEXT | NULL |
| ControllerGrantedAt | TEXT | NULL |
| ControllerLeaseExpiresAt | TEXT | NULL |

- 唯一索引：`(UserId)`——One User One Persistent Workspace，对应 `InMemoryWorkspaceRepository._byUserId`
- `terminal_settings`：EF Core `OwnsOne + ToJson`，把 6 字段 DTO 序列化为单列 JSON。示例值：
  ```json
  {"fontFamily":"Cascadia Mono","fontSize":14,"colorScheme":"Campbell","backgroundColor":"#0C0C0C","foregroundColor":"#CCCCCC","cursorColor":"#FFFFFF"}
  ```
- `browser_settings` / `preferences`：同 `OwnsOne + ToJson` 模式，单列 JSON（BrowserSettingsDto / WorkspacePreferencesDto）。列允许 NULL——读取 NULL 时回退领域模型默认值。既有库（建库时无此列）由 `Program.cs` 启动时 `ALTER TABLE ... ADD COLUMN ... TEXT NULL` 增量补齐（见 [`RemoteOS.Settings.md`](./RemoteOS.Settings.md) §4.2 / [`RemoteOS.Browser.md`](./RemoteOS.Browser.md) §3.3）。

### 5.3 devices

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| Name | TEXT | NOT NULL，≤128 |
| Platform | TEXT | NOT NULL，≤32（小写字符串） |
| ClientVersion | TEXT | ≤64 |
| LastLoginAt | TEXT | NULL |

- 唯一索引：`(Name, Platform)`——对应 `InMemoryDeviceRepository._byKey`

> Guid 主键存 TEXT；DateTimeOffset 存 TEXT（ISO 8601，SQLite 原生 datetime 处理）。索引与现有内存仓储的 `_byName` / `_byUserId` / `_byKey` 字典键一一对应，保证切换实现后查询语义不变。

---

## 6. 仓储层

### 6.1 接口不变

保留现有接口（[`RemoteOS.Server/Storage/`](../RemoteOS.Server/Storage/)）：
- `IUserRepository`：FindByUsername / FindById / Add / UpdateLastLogin
- `IWorkspaceRepository`：FindByUserId / FindById / Add / Update
- `IDeviceRepository`：FindByNameAndPlatform / FindById / Add / Update
- `ISessionRepository`：始终 `InMemorySessionRepository`（Session 不持久化）

### 6.2 新增 EF 实现

[`RemoteOS.Server/Storage/Sqlite/`](../RemoteOS.Server/Storage/Sqlite/)：
- [`RemoteOsDbContext`](../RemoteOS.Server/Storage/Sqlite/RemoteOsDbContext.cs)：`DbSet<User/Workspace/Device>` + `OnModelCreating`
- `SqliteUserRepository` / `SqliteWorkspaceRepository` / `SqliteDeviceRepository`：注入 DbContext，用 EF 查询实现接口

实现要点：
- **查询**用 `AsNoTracking()` 返回 detached 实体（避免跨请求 stale tracking）。
- **Add / Update** 显式 `SaveChanges()` 持久化（与 InMemory 立即生效语义一致）。
- **UpdateLastLogin** 用 tracking 查询（`Find`）加载后修改属性，SaveChanges 检测变更。
- **TerminalSettings 兜底**：读取时若 JSON 列为 null（防御旧数据），仓储回退 `TerminalSettingsDto.Default`。

### 6.3 生命周期

仓储与 DbContext 均为 **Scoped**（每请求一个 DbContext）。Minimal API `[FromServices]` 每请求创建 scope，兼容。Singleton 服务（`AuthSessionStore` / `JwtTokenService` / `TerminalSessionManager`）不依赖仓储，不受影响。

---

## 7. 配置项

[`appsettings.json`](../RemoteOS.Server/appsettings.json) 新增 `Storage` 节：

```json
"Storage": {
  "Provider": "sqlite",
  "DatabasePath": "data/remoteos.db"
}
```

| 项 | 默认 | 说明 |
|----|------|------|
| Provider | `sqlite` | `sqlite`（EF Core + SQLite，默认）或 `memory`（内存仓储，开发回退） |
| DatabasePath | `data/remoteos.db` | SQLite 文件相对路径（相对 ContentRoot）；启动时自动建目录 |

绑定到 [`StorageOptions`](../RemoteOS.Server/Storage/StorageOptions.cs)。`Program.cs` 按 Provider 注册：
- `sqlite`：`AddDbContext<RemoteOsDbContext>(UseSqlite)` + `AddScoped<I*Repository, Sqlite*Repository>` + 启动建库
- `memory`：`AddSingleton<I*Repository, InMemory*Repository>`（开发回退，重启丢失）

---

## 8. 建库策略

启动时（`Program.cs`，`app.Build()` 之后）：

```csharp
if (storageProvider == "sqlite")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RemoteOsDbContext>();
    db.Database.EnsureCreated();
}
```

- **当前**：`EnsureCreated()`——零工具依赖，幂等（库已存在则跳过），适合 MVP 稳定 schema。
- **未来演进**：当 schema 需要变更时，切换为 EF Core Migrations——`dotnet ef migrations add` 生成迁移 + `db.Database.MigrateAsync()` 应用。注意 `EnsureCreated` 与 Migrations 互斥（EnsureCreated 不建 `__EFMigrationsHistory` 表，切换前需删除旧库或手动初始化迁移）。

---

## 9. 与登录流程的交互

[`AuthEndpoints.Login`](../RemoteOS.Server/Endpoints/AuthEndpoints.cs) 的 FindOrCreate 流程在持久化后变为：

```text
宿主 OS 认证（IIdentityProvider.Verify）
    │
    ├─ FindByUsername(username, platform)
    │     └─ 命中 SQLite 既有 User → 复用（保留 CreatedAt 历史）
    │     └─ 未命中 → Add 新 User → 写入 SQLite
    │
    ├─ FindByUserId(user.Id)
    │     └─ 命中 SQLite 既有 Workspace → 复用（带回已持久化的 TerminalSettings）★ 配置持久化生效
    │     └─ 未命中 → Add 新 Workspace（TerminalSettings=Default）→ 写入 SQLite
    │
    ├─ FindByNameAndPlatform(deviceName, platform)
    │     └─ 命中 → 更新 ClientVersion/LastLoginAt
    │     └─ 未命中 → Add 新 Device → 写入 SQLite
    │
    ├─ 新建 Session（内存，每次登录新建）
    ├─ 设为 Controller（Update Workspace → SQLite）
    └─ Issue JWT
```

**配置持久化闭环**：
1. 用户登录 → Workspace 以 Default TerminalSettings 写入 SQLite
2. 用户改终端配置 → `PUT terminal-settings` → `workspace.TerminalSettings = normalized; Update(workspace)` → SQLite 更新 `terminal_settings` JSON 列
3. **Server 重启** → SQLite 保留
4. 用户重新登录 → `FindByUserId` 命中既有 Workspace → **带回改过的 TerminalSettings** → 配置恢复

---

## 10. 并发与连接

- EF Core SQLite provider 默认使用连接池（`Microsoft.Data.Sqlite` 内置），per-operation 连接。
- 写并发：SQLite 默认 `journal_mode=delete`，单写者。MVP 单服务器场景写并发极低（登录/改配置），可接受。未来若需提升读写并发，可启用 WAL（`PRAGMA journal_mode=WAL`）。
- 仓储 Scoped + 每请求独立 DbContext，无跨请求共享状态。

---

## 11. 已知警告

- `NU1903`：`SQLitePCLRaw.lib.e_sqlite3` 2.1.11（EF Core Sqlite 传递依赖）有已知高严重性漏洞 [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)。此为 EF Core 10.0.10 捆绑的传递依赖版本，属既有 NU1903 警告类（与 Microsoft.OpenApi 警告同类），MVP 阶段可接受；后续关注 EF Core 版本更新是否修复。

---

## 12. AI Agent 理解规则

实现 RemoteOS.Server 持久化时必须遵守：

- **持久化范围**：持久化 User / Workspace（含 TerminalSettings / BrowserSettings / Preferences）/ Device / Bookmark / HistoryEntry。**不要**把 Session、refresh token（AuthSessionStore）、PTY 会话（TerminalSessionManager）写入数据库——它们是运行时/安全/进程状态，持久化会引入不一致或安全风险。
- **接口稳定**：新增 EF 实现时**不要**改动 `IUserRepository` / `IWorkspaceRepository` / `IDeviceRepository` / `IBrowserRepository` 接口；端点与领域模型不动。
- **配置存 JSON 列**：TerminalSettings / BrowserSettings / Preferences 均用 `OwnsOne + ToJson`，**不要**拆成独立列——配置应可演进，新增字段不改 schema。新增 Workspace 级 JSON 列时，`Program.cs` 启动时检测 `pragma_table_info` 并 `ALTER TABLE ... ADD COLUMN ... TEXT NULL` 增量补齐既有库（`EnsureCreated` 不为已存在 db 追加列）。
- **索引对齐**：SQLite 唯一索引必须与内存仓储的字典键（`_byName`/`_byUserId`/`_byKey`）一一对应，保证切换实现后查询语义不变。
- **Session 始终内存**：`ISessionRepository` 永远是 `InMemorySessionRepository`，不接入 DbContext。
- **建库用 EnsureCreated（当前）**：未来切 Migrations 时需先删旧库或初始化迁移历史（EnsureCreated 与 Migrations 互斥）。
- **配置驱动**：通过 `Storage:Provider` 切换 sqlite/memory，**不要**硬编码。

---

## 13. 相关文档

- [`RemoteOS.md`](./RemoteOS.md) — 项目结构、当前进度（§4.9 Server）
- [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md) — User/Workspace/Device 模型（§22 Persistent Workspace）
- [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md) / [`RemoteOS.Login.md`](./RemoteOS.Login.md) — 登录流程、身份映射
- [`RemoteOS.Terminal.md`](./RemoteOS.Terminal.md) — 终端应用（TerminalSettings 的消费端）
- [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md) — 架构原则
