using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using System.Security.Claims;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Workspace;
using Server.Domain;
using Server.Identity;
using Server.Storage;

namespace Server.Endpoints;

/// <summary>认证 REST 端点。路由常量见 AuthApiRoutes。错误统一返回 RFC 7807 ProblemDetails，
/// 错误码通过 type URI 传递（ProblemDetails 无 Errors 字段，见 Protocol.md）。</summary>
public static class AuthEndpoints
{
    private const string ProblemBase = "https://remoteos.app/problems/";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(AuthApiRoutes.Login, (
                LoginRequest req,
                HttpContext http,
                IIdentityProvider idp,
                IUserRepository users,
                IWorkspaceRepository wss,
                ISessionRepository sess,
                IDeviceRepository devs,
                JwtTokenService jwt) =>
            {
                if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                    return Problem(http, 400, "invalid-input", "Invalid input", "Username and password are required.");

                var result = idp.Verify(req.Username, req.Password);
                if (!result.Success)
                    return MapCredentialErrorToProblem(http, result);

                var info = idp.GetUserInfo(req.Username);
                var platform = req.ClientPlatform;
                var now = DateTimeOffset.UtcNow;

                // 查/建 User（One User，按 username+platform 索引）
                var user = users.FindByUsername(req.Username, platform)
                          ?? users.Add(new User
                          {
                              Id = Guid.NewGuid(),
                              Username = req.Username,
                              Platform = platform,
                              PlatformIdentity = info.Uid,
                              CreatedAt = now,
                          });

                // 查/建 Workspace（One User One Persistent，见 Workspace.md §4）
                var ws = wss.FindByUserId(user.Id)
                       ?? wss.Add(new Workspace
                       {
                           Id = Guid.NewGuid(),
                           UserId = user.Id,
                           Name = $"{user.Username} Workspace",
                           State = WorkspaceState.Running,
                           CreatedAt = now,
                       });

                // 查/建 Device（按 name+platform 复用，更新版本与登录时间）
                var platformStr = req.ClientPlatform.ToString().ToLowerInvariant();
                var device = devs.FindByNameAndPlatform(req.DeviceName, platformStr);
                if (device is null)
                {
                    device = devs.Add(new Device
                    {
                        Id = Guid.NewGuid(),
                        Name = req.DeviceName,
                        Platform = platformStr,
                        ClientVersion = req.ClientVersion,
                    });
                }
                device.ClientVersion = req.ClientVersion;
                device.LastLoginAt = now;
                devs.Update(device);

                // 新建 Session（每次登录新建，Session ≠ Workspace）
                var session = sess.Add(new Session
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = ws.Id,
                    DeviceId = device.Id,
                    CreatedAt = now,
                    LastActiveAt = now,
                    Status = SessionStatus.Active,
                });

                // 该设备成为 Controller（Grace Period 5 分钟，见 Workspace.md §19）
                ws.ControllerDeviceId = device.Id;
                ws.ControllerGrantedAt = now;
                ws.ControllerLeaseExpiresAt = now.AddMinutes(5);
                ws.State = WorkspaceState.Running;
                wss.Update(ws);

                users.UpdateLastLogin(user.Id, now);

                var role = DeviceRole.Controller;
                var tokens = jwt.Issue(user, ws, device, role);

                return Results.Ok(new LoginResponse(
                    user.ToDto(), ws.ToDto(), session.ToDto(), device.ToDto(), tokens, role, CreateServerDescriptor()));
            })
            .WithTags("Auth");

        app.MapPost(AuthApiRoutes.Refresh, (
                RefreshTokenRequest req,
                HttpContext http,
                AuthSessionStore sessions,
                IUserRepository users,
                IWorkspaceRepository wss,
                IDeviceRepository devs,
                JwtTokenService jwt) =>
            {
                if (string.IsNullOrEmpty(req.RefreshToken) || !sessions.IsValid(req.RefreshToken))
                    return Problem(http, 401, "invalid-credential", "Invalid credentials", "The refresh token is invalid or has expired.");

                if (!sessions.TryGet(req.RefreshToken, out var rec))
                    return Problem(http, 401, "invalid-credential", "Invalid credentials", "The refresh token record does not exist.");

                sessions.Revoke(req.RefreshToken);  // 旧 refresh 作废（一次性）

                var user = users.FindById(rec.UserId);
                var ws = wss.FindById(rec.WorkspaceId);
                var device = devs.FindById(rec.DeviceId);
                if (user is null || ws is null || device is null)
                    return Problem(http, 401, "invalid-credential", "Invalid credentials", "The session context is no longer valid.");

                var role = ws.ControllerDeviceId == device.Id ? DeviceRole.Controller : DeviceRole.Observer;
                var tokens = jwt.Issue(user, ws, device, role);
                return Results.Ok(new RefreshTokenResponse(tokens));
            })
            .WithTags("Auth");

        app.MapPost(AuthApiRoutes.Logout, (LogoutRequest? req, AuthSessionStore sessions) =>
            {
                if (!string.IsNullOrEmpty(req?.RefreshToken))
                    sessions.Revoke(req.RefreshToken);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithTags("Auth");

        app.MapGet(AuthApiRoutes.Me, (ClaimsPrincipal principal, IUserRepository users, HttpContext http) =>
            {
                var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                          ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var userId))
                    return Problem(http, 401, "invalid-credential", "Invalid credentials", "The token is missing a user identity.");

                var user = users.FindById(userId);
                if (user is null)
                    return Results.NotFound();

                return Results.Ok(user.ToDto());
            })
            .RequireAuthorization()
            .WithTags("Auth");

        return app;
    }

    private static ServerDescriptorDto CreateServerDescriptor()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var capabilities = new List<string>
        {
            ServerCapabilities.Files,
            ServerCapabilities.Metrics,
            ServerCapabilities.Processes,
            ServerCapabilities.Terminal,
        };
        if (!isWindows)
        {
            capabilities.Add(ServerCapabilities.PosixPermissions);
            capabilities.Add(ServerCapabilities.Firewall);
        }

        return new ServerDescriptorDto(isWindows ? PlatformKind.Windows : PlatformKind.Linux, capabilities);
    }

    /// <summary>CredentialError → ProblemDetails 映射。type URI 作为错误码，客户端按 type 匹配 UI 文案。</summary>
    private static IResult MapCredentialErrorToProblem(HttpContext http, CredentialVerifyResult r) => r.Error switch
    {
        CredentialError.BadCredentials or CredentialError.NoSuchUser =>
            Problem(http, 401, "invalid-credential", "Invalid credentials", r.Message),
        CredentialError.AccountLockedOut =>
            Problem(http, 423, "account-locked", "Account locked", r.Message),
        CredentialError.AccountDisabled =>
            Problem(http, 403, "account-disabled", "Account disabled", r.Message),
        CredentialError.PasswordExpired =>
            Problem(http, 403, "password-expired", "Password expired", r.Message),
        CredentialError.AccountExpired =>
            Problem(http, 403, "account-expired", "Account expired", r.Message),
        CredentialError.AccountRestriction =>
            Problem(http, 403, "account-restriction", "Account restricted", r.Message),
        CredentialError.InvalidInput =>
            Problem(http, 400, "invalid-input", "Invalid input", r.Message),
        _ => Problem(http, 500, "auth-failed", "Authentication failed", r.Message),
    };

    private static IResult Problem(HttpContext http, int status, string typeSuffix, string title, string detail)
        => Results.Problem(detail: detail, statusCode: status,
            title: ApiLocalizer.Get(http, typeSuffix, title), type: ProblemBase + typeSuffix);
}
