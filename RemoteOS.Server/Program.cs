using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using RoyalTerminal.Terminal;
using Server.Endpoints;
using Server.Hubs;
using Server.Identity;
using Server.Storage;

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

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
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
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/hubs/terminals"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// 身份认证 Provider（按宿主 OS 平台选择，见 Authentication.md §1.1）
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    builder.Services.AddSingleton<IIdentityProvider, WindowsLogonProvider>();
else
    builder.Services.AddSingleton<IIdentityProvider, LinuxPamProvider>();

// 内存仓储（Singleton，重启丢失；MVP 阶段可接受）
builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
builder.Services.AddSingleton<IWorkspaceRepository, InMemoryWorkspaceRepository>();
builder.Services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();

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

// CORS（开发期允许客户端跨域）
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddOpenApi();

var app = builder.Build();

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
app.MapWorkspaceEndpoints();
app.MapHub<TerminalHub>("/hubs/terminals");

app.Run();
