using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Workspace;

namespace Client.Services.Auth;

/// <summary>IAuthSession 实现。勾选“记住此设备”时保存受 DPAPI 保护的会话；lock 保护并发登录。</summary>
public sealed class AuthSession : IAuthSession
{
    private readonly IRemoteOsClient _client;
    private readonly IRememberedSessionStore _rememberedSessionStore;
    private readonly object _gate = new();
    private bool _rememberDevice;

    public AuthSession(IRemoteOsClient client, IRememberedSessionStore rememberedSessionStore)
    {
        _client = client;
        _rememberedSessionStore = rememberedSessionStore;
    }

    public AuthSessionState State { get; private set; } = AuthSessionState.Unauthenticated;
    public string? ServerUrl { get; private set; }
    public AuthTokens? Tokens { get; private set; }
    public UserDto? CurrentUser { get; private set; }
    public WorkspaceDto? CurrentWorkspace { get; private set; }
    public SessionDto? CurrentSession { get; private set; }
    public DeviceDto? CurrentDevice { get; private set; }
    public DeviceRole AssignedRole { get; private set; } = DeviceRole.Observer;

    public event EventHandler<AuthSessionStateChangedEventArgs>? StateChanged;

    public async Task<LoginResponse> LoginAsync(
        string serverUrl,
        LoginRequest request,
        bool rememberDevice,
        CancellationToken ct = default)
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
            Apply(response, serverUrl);
            _rememberDevice = rememberDevice;
            if (rememberDevice)
                await _rememberedSessionStore.SaveAsync(RememberedSession.From(serverUrl, response), ct);
            else
                await _rememberedSessionStore.ClearAsync(ct);
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

    public async Task<bool> TryRestoreAsync(CancellationToken ct = default)
    {
        var remembered = await _rememberedSessionStore.LoadAsync(ct);
        if (remembered is null || remembered.Tokens.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (remembered is not null)
                await _rememberedSessionStore.ClearAsync(ct);
            return false;
        }

        lock (_gate)
        {
            if (State != AuthSessionState.Unauthenticated)
                return false;
            State = AuthSessionState.Connecting;
        }
        RaiseStateChanged();

        try
        {
            var refreshed = await _client.RefreshAsync(remembered.ServerUrl, remembered.Tokens.RefreshToken, ct);
            Apply(remembered.ToLoginResponse(refreshed.Tokens), remembered.ServerUrl);
            _rememberDevice = true;
            await _rememberedSessionStore.SaveAsync(remembered with { Tokens = refreshed.Tokens }, ct);
            State = AuthSessionState.Authenticated;
            RaiseStateChanged();
            return true;
        }
        catch
        {
            await _rememberedSessionStore.ClearAsync(ct);
            Reset();
            return false;
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
        finally
        {
            await _rememberedSessionStore.ClearAsync(ct);
            Reset();
        }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (ServerUrl is null || Tokens is null) return false;
        try
        {
            var resp = await _client.RefreshAsync(ServerUrl, Tokens.RefreshToken, ct);
            Tokens = resp.Tokens;
            if (_rememberDevice)
                await _rememberedSessionStore.SaveAsync(RememberedSession.From(ServerUrl, this), ct);
            return true;
        }
        catch
        {
            await _rememberedSessionStore.ClearAsync(ct);
            Reset();
            return false;
        }
    }

    private void Apply(LoginResponse response, string serverUrl)
    {
        ServerUrl = serverUrl;
        Tokens = response.Tokens;
        CurrentUser = response.User;
        CurrentWorkspace = response.Workspace;
        CurrentSession = response.Session;
        CurrentDevice = response.Device;
        AssignedRole = response.AssignedRole;
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
        _rememberDevice = false;
        State = AuthSessionState.Unauthenticated;
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
        => StateChanged?.Invoke(this, new AuthSessionStateChangedEventArgs(State));
}
