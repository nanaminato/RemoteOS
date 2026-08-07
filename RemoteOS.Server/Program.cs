using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RemoteOS.Protocol.Browser;
using RoyalTerminal.Terminal;
using Server.Browser;
using Server.Endpoints;
using Server.Hubs;
using Server.Identity;
using Server.Storage;
using Server.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// 序列化：与 RemoteOsJsonOptions.Default 对齐（camelCase + 枚举字符串），保证线协议一致
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    opts.SerializerOptions.PropertyNameCaseInsensitive = true;
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// JWT 配置（绑定 + 启动校验）
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwtCfg = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
    ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");
if (string.IsNullOrWhiteSpace(jwtCfg.Secret) || jwtCfg.Secret.Length < 32)
    throw new InvalidOperationException("Jwt:Secret 必须至少 32 字符（HMACSHA256 要求 ≥256 位）。");
if (builder.Environment.IsProduction() && jwtCfg.Secret == JwtOptions.DefaultInsecureSecret)
    throw new InvalidOperationException("Production 环境必须替换默认 Jwt:Secret。");

builder.Services.AddSingleton<AuthSessionStore>();
builder.Services.AddSingleton<JwtTokenService>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = RemoteOsAuthSchemes.User;
        options.DefaultChallengeScheme = RemoteOsAuthSchemes.User;
    })
    .AddJwtBearer(RemoteOsAuthSchemes.User, opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtCfg.Issuer,
            ValidAudience = jwtCfg.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg.Secret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // SignalR WebSocket 升级请求无法可靠携带 Authorization 头（.NET 客户端走头，但补齐 query 兜底）。
        // 对终端 Hub 路径，从查询串 access_token 读取令牌注入 JwtBearer，修复 WebSocket 升级 401。
        opts.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.HasClaim(RemoteOsAuthSchemes.TokenTypeClaim, RemoteOsAuthSchemes.FileCapabilityTokenType) == true)
                    context.Fail("File capability tokens cannot be used as user access tokens.");
                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/hubs/terminals"))
                {
                    context.Token = accessToken;
                }
                else if (path.StartsWithSegments(BrowserApiRoutes.LocalPortForwardingPrefix))
                {
                    var proxyToken = context.Request.Query[BrowserApiRoutes.LocalPortForwardingTokenQuery];
                    context.Token = !string.IsNullOrEmpty(proxyToken)
                        ? proxyToken
                        : context.Request.Cookies[BrowserApiRoutes.LocalPortForwardingAuthCookie];
                }
                return Task.CompletedTask;
            }
        };
    })
    .AddJwtBearer(RemoteOsAuthSchemes.FileCapability, opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtCfg.Issuer,
            ValidAudience = jwtCfg.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg.Secret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        opts.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.HasClaim(RemoteOsAuthSchemes.TokenTypeClaim, RemoteOsAuthSchemes.FileCapabilityTokenType) != true)
                    context.Fail("This endpoint requires a file capability token.");
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization(options =>
{
    foreach (var policyName in new[]
             {
                 Server.Files.FileAuthorizationPolicies.List,
                 Server.Files.FileAuthorizationPolicies.Read,
                 Server.Files.FileAuthorizationPolicies.Write,
                 Server.Files.FileAuthorizationPolicies.Manage,
             })
    {
        var requiredScope = Server.Files.FileAuthorizationPolicies.ScopeForPolicy(policyName);
        options.AddPolicy(policyName, policy => policy
            .AddAuthenticationSchemes(RemoteOsAuthSchemes.User, RemoteOsAuthSchemes.FileCapability)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                !context.User.HasClaim(RemoteOsAuthSchemes.TokenTypeClaim, RemoteOsAuthSchemes.FileCapabilityTokenType)
                || context.User.HasClaim(RemoteOsAuthSchemes.ScopeClaim, requiredScope)));
    }
});

// The forwarding client has no cookie jar and never follows redirects automatically.
// This prevents requests or session state from one RemoteOS user reaching another user's
// loopback service; redirects are validated and rewritten by LocalPortForwarder instead.
builder.Services.AddHttpClient(LocalPortForwarder.HttpClientName, client =>
    client.Timeout = TimeSpan.FromMinutes(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });
builder.Services.AddSingleton<LocalPortForwarder>();

// 身份认证 Provider（按宿主 OS 平台选择，见 Authentication.md §1.1）
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    builder.Services.AddSingleton<IIdentityProvider, WindowsLogonProvider>();
else
    builder.Services.AddSingleton<IIdentityProvider, LinuxPamProvider>();

// 任务管理器：系统指标采集 Provider（按宿主 OS 平台选择，与 IIdentityProvider 同模式）。
// CPU/内存平台特定（Linux 读 /proc；Windows 走 P/Invoke），磁盘/网络/GPU/进程跨平台共享。
// 以宿主 OS 进程身份读取，复用宿主用户/权限（不另建 ACL）。Singleton 持相邻采样差分状态。
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    builder.Services.AddSingleton<Server.SystemMonitor.ISystemMetricsProvider, Server.SystemMonitor.WindowsMetricsProvider>();
else
    builder.Services.AddSingleton<Server.SystemMonitor.ISystemMetricsProvider, Server.SystemMonitor.LinuxMetricsProvider>();

// 持久化仓储：按 Storage:Provider 选择 sqlite（EF Core + SQLite，默认）或 memory（内存，开发回退）。
// User / Workspace(含 TerminalSettings) / Device 持久化；Session 始终内存（连接关系，运行时状态，重启失效合理）。
// 详见 docs/RemoteOS.Storage.md。
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
var storageOpts = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
var storageProvider = string.IsNullOrWhiteSpace(storageOpts.Provider) ? "sqlite" : storageOpts.Provider.ToLowerInvariant();

if (storageProvider == "sqlite")
{
    // 数据库文件路径相对 ContentRoot，自动建目录
    var dbPath = Path.Combine(builder.Environment.ContentRootPath, storageOpts.DatabasePath);
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir))
        Directory.CreateDirectory(dbDir);
    builder.Services.AddDbContext<RemoteOsDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
    // 仓储为 Scoped（依赖 Scoped 的 DbContext）；Minimal API [FromServices] 每请求创建 scope，兼容
    builder.Services.AddScoped<IUserRepository, SqliteUserRepository>();
    builder.Services.AddScoped<IWorkspaceRepository, SqliteWorkspaceRepository>();
    builder.Services.AddScoped<IDeviceRepository, SqliteDeviceRepository>();
    builder.Services.AddScoped<IBrowserRepository, SqliteBrowserRepository>();
}
else
{
    // memory：开发回退（重启丢失）
    builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
    builder.Services.AddSingleton<IWorkspaceRepository, InMemoryWorkspaceRepository>();
    builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();
    builder.Services.AddSingleton<IBrowserRepository, InMemoryBrowserRepository>();
}
// Session 始终内存（连接关系，不持久化）
builder.Services.AddSingleton<ISessionRepository, InMemorySessionRepository>();

// 终端：服务端 PTY 工厂（Windows ConPTY / Unix forkpty）+ 持久会话管理器 + SignalR Hub。
// AddSignalR 由 Microsoft.NET.Sdk.Web 隐式 FrameworkReference 提供，无需额外 NuGet。
builder.Services.AddSingleton<IPtyFactory, Server.Terminal.PlatformPtyFactory>();
builder.Services.AddSingleton<Server.Terminal.TerminalSessionManager>();
// 以 JWT sub claim 作为 Hub UserIdentifier，供 TerminalHub 按用户索引/过滤持久会话。
builder.Services.AddSingleton<IUserIdProvider, Server.Terminal.TerminalUserIdProvider>();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = null);

// 文件管理：以宿主 OS 进程身份执行 IO，复用宿主用户/权限（不另建 ACL——见 project_memory 硬约束）。
// LocalFileService 移植自 Jaya FileSystemService 的目录枚举逻辑并扩展为完整文件操作；平台感知（Windows 盘符 / Linux "/" 根）。
builder.Services.AddSingleton<Server.Files.IFileService, Server.Files.LocalFileService>();
builder.Services.AddSingleton<Server.Files.MediaLeaseStore>();

// CORS（开发期允许客户端跨域）
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddOpenApi();

var app = builder.Build();

// 启动时建库/建表（SQLite 模式）。EnsureCreated 零工具依赖，适合 MVP 稳定 schema；
// 未来 schema 需演进时切换为 EF Core Migrations（db.Database.MigrateAsync）。
// 注意：EnsureCreated 只在库不存在时建表——已存在的 db 不会追加新表（如本次新增的 bookmarks/history_entries）。
// 为兼容既有部署（保留测试数据），追加 CREATE TABLE IF NOT EXISTS 增量补齐浏览器相关表。
if (storageProvider == "sqlite")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RemoteOsDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated does not add columns to an existing database. Browser settings are an
    // owned JSON value on Workspace, so upgrade older deployments before reading/writing it.
    var hasBrowserSettings = db.Database.SqlQueryRaw<long>(
        "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('workspaces') WHERE name = 'browser_settings'").Single() > 0;
    if (!hasBrowserSettings)
        db.Database.ExecuteSqlRaw("ALTER TABLE \"workspaces\" ADD COLUMN \"browser_settings\" TEXT NULL;");

    // 用户偏好（壁纸/主题/时间格式/语言/区域/默认程序）——与 browser_settings 同模式：OwnsOne ToJson 单列。
    var hasPreferences = db.Database.SqlQueryRaw<long>(
        "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('workspaces') WHERE name = 'preferences'").Single() > 0;
    if (!hasPreferences)
        db.Database.ExecuteSqlRaw("ALTER TABLE \"workspaces\" ADD COLUMN \"preferences\" TEXT NULL;");

    var hasWindowLayouts = db.Database.SqlQueryRaw<long>(
        "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('workspaces') WHERE name = 'window_layouts'").Single() > 0;
    if (!hasWindowLayouts)
        db.Database.ExecuteSqlRaw("ALTER TABLE \"workspaces\" ADD COLUMN \"window_layouts\" TEXT NULL;");

    // 增量补齐：仅当表不存在时创建（与 EF Core 模型一致，索引/列类型对齐 OnModelCreating）。
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "bookmarks" (
            "Id" TEXT NOT NULL PRIMARY KEY,
            "UserId" TEXT NOT NULL,
            "Title" TEXT,
            "Url" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_bookmarks_UserId_Url" ON "bookmarks" ("UserId", "Url");
        CREATE INDEX IF NOT EXISTS "IX_bookmarks_UserId" ON "bookmarks" ("UserId");

        CREATE TABLE IF NOT EXISTS "history_entries" (
            "Id" TEXT NOT NULL PRIMARY KEY,
            "UserId" TEXT NOT NULL,
            "Title" TEXT,
            "Url" TEXT NOT NULL,
            "VisitCount" INTEGER NOT NULL,
            "FirstVisitedAt" TEXT NOT NULL,
            "LastVisitedAt" TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_history_entries_UserId_Url" ON "history_entries" ("UserId", "Url");
        CREATE INDEX IF NOT EXISTS "IX_history_entries_UserId_LastVisitedAt" ON "history_entries" ("UserId", "LastVisitedAt");
        """);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors();
    // 开发期不强制 HTTPS 重定向，方便客户端用 http 测试
}
else
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();
app.MapFileEndpoints();
app.MapAppCapabilityEndpoints();
app.MapWorkspaceEndpoints();
app.MapBrowserEndpoints();
app.MapSystemMonitorEndpoints();
app.MapHub<TerminalHub>("/hubs/terminals");

app.Run();
