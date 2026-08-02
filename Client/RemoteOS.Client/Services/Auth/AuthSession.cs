using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Workspace;

namespace Client.Services.Auth;

/// <summary>IAuthSession 实现。单例，仅内存。lock 保护并发登录。</summary>
public sealed class AuthSession : IAuthSession
{
    private readonly IRemoteOsClient _client;
    private readonly object _gate = new();

    public AuthSession(IRemoteOsClient client) => _client = client;

    public AuthSessionState State { get; private set; } = AuthSessionState.Unauthenticated;
    public string? ServerUrl { get; private set; }
    public AuthTokens? Tokens { get; private set; }
    public UserDto? CurrentUser { get; private set; }
    public WorkspaceDto? CurrentWorkspace { get; private set; }
    public SessionDto? CurrentSession { get; private set; }
    public DeviceDto? CurrentDevice { get; private set; }
    public DeviceRole AssignedRole { get; private set; } = DeviceRole.Observer;

    public event EventHandler<AuthSessionStateChangedEventArgs>? StateChanged;

    public async Task<LoginResponse> LoginAsync(string serverUrl, LoginRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (State == AuthSessionState.Connecting)
                throw new InvalidOperationException("已有登录请求进行中");
            State = AuthSessionState.Connecting;
        }
        RaiseStateChanged();

        try
        {
            var response = await _client.LoginAsync(serverUrl, request, ct);
            ServerUrl = serverUrl;
            Tokens = response.Tokens;
            CurrentUser = response.User;
            CurrentWorkspace = response.Workspace;
            CurrentSession = response.Session;
            CurrentDevice = response.Device;
            AssignedRole = response.AssignedRole;
            State = AuthSessionState.Authenticated;
            RaiseStateChanged();
            return response;
        }
        catch
        {
            State = AuthSessionState.Unauthenticated;
            RaiseStateChanged();
            throw;
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var url = ServerUrl;
        var tokens = Tokens;
        if (url is null || tokens is null)
        {
            Reset();
            return;
        }
        try { await _client.LogoutAsync(url, tokens.AccessToken, tokens.RefreshToken, ct); }
        finally { Reset(); }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (ServerUrl is null || Tokens is null) return false;
        try
        {
            var resp = await _client.RefreshAsync(ServerUrl, Tokens.RefreshToken, ct);
            Tokens = resp.Tokens;
            return true;
        }
        catch
        {
            Reset();
            return false;
        }
    }

    private void Reset()
    {
        ServerUrl = null;
        Tokens = null;
        CurrentUser = null;
        CurrentWorkspace = null;
        CurrentSession = null;
        CurrentDevice = null;
        AssignedRole = DeviceRole.Observer;
        State = AuthSessionState.Unauthenticated;
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
        => StateChanged?.Invoke(this, new AuthSessionStateChangedEventArgs(State));
}
