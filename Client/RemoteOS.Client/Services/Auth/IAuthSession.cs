using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Workspace;

namespace Client.Services.Auth;

/// <summary>客户端认证会话。持有当前登录上下文（Tokens/User/Workspace/Session/Device/Role）；
/// 勾选“记住此设备”时，刷新令牌会受当前 Windows 用户的 DPAPI 保护后持久化。供 LoginViewModel 与桌面 Shell 共享。</summary>
public interface IAuthSession
{
    AuthSessionState State { get; }
    string? ServerUrl { get; }
    AuthTokens? Tokens { get; }
    UserDto? CurrentUser { get; }
    WorkspaceDto? CurrentWorkspace { get; }
    SessionDto? CurrentSession { get; }
    DeviceDto? CurrentDevice { get; }
    DeviceRole AssignedRole { get; }

    /// <summary>状态变化（Connecting / Authenticated / Unauthenticated）。</summary>
    event EventHandler<AuthSessionStateChangedEventArgs>? StateChanged;

    /// <summary>登录。serverUrl 形如 "http://localhost:5090"。成功缓存全部上下文并触发 StateChanged。</summary>
    Task<LoginResponse> LoginAsync(
        string serverUrl,
        LoginRequest request,
        bool rememberDevice,
        CancellationToken ct = default);

    /// <summary>Attempts to resume a session saved for the current OS user. Returns false when none is saved or it has expired.</summary>
    Task<bool> TryRestoreAsync(CancellationToken ct = default);

    /// <summary>登出（吊销 RefreshToken，清空上下文）。</summary>
    Task LogoutAsync(CancellationToken ct = default);

    /// <summary>用 RefreshToken 换新令牌对。失败返回 false 并重置会话。</summary>
    Task<bool> RefreshAsync(CancellationToken ct = default);
}
