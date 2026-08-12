using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Workspace;

namespace Client.Services.Auth;

/// <summary>Holds the current authenticated session and the locally remembered RemoteOS connections.</summary>
public sealed class AuthSession : IAuthSession
{
    private readonly IRemoteOsClient _client;
    private readonly IRememberedSessionStore _rememberedSessionStore;
    private readonly object _gate = new();

    public AuthSession(IRemoteOsClient client, IRememberedSessionStore rememberedSessionStore)
    {
        _client = client;
        _rememberedSessionStore = rememberedSessionStore;
    }

    public AuthSessionState State { get; private set; } = AuthSessionState.Unauthenticated;
    public string? ServerUrl { get; private set; }
    public AuthTokens? Tokens { get; private set; }
    public UserDto? CurrentUser { get; private set; }
    public ServerDescriptorDto? CurrentServer { get; private set; }
    public WorkspaceDto? CurrentWorkspace { get; private set; }
    public SessionDto? CurrentSession { get; private set; }
    public DeviceDto? CurrentDevice { get; private set; }
    public DeviceRole AssignedRole { get; private set; } = DeviceRole.Observer;

    public event EventHandler<AuthSessionStateChangedEventArgs>? StateChanged;

    public async Task<LoginResponse> LoginAsync(
        string serverUrl,
        LoginRequest request,
        bool rememberServer,
        bool rememberPassword,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (State == AuthSessionState.Connecting)
                throw new InvalidOperationException("A login request is already in progress.");
            State = AuthSessionState.Connecting;
        }
        RaiseStateChanged();

        try
        {
            var response = await _client.LoginAsync(serverUrl, request, ct);
            Apply(response, serverUrl);

            RememberedProfileSaveResult? saveResult = null;
            if (rememberServer)
            {
                saveResult = await _rememberedSessionStore.UpsertAsync(
                    new SavedLoginProfile(serverUrl, request.Username, rememberPassword ? request.Password : null, DateTimeOffset.UtcNow), ct);
            }

            State = AuthSessionState.Authenticated;
            // Saving a local credential is best-effort: it must not turn a successful remote login into an error,
            // but the UI needs the outcome so it can explain why the password will not be prefilled next time.
            RaiseStateChanged(saveResult);
            return response;
        }
        catch
        {
            State = AuthSessionState.Unauthenticated;
            RaiseStateChanged();
            throw;
        }
    }

    public async Task<IReadOnlyList<SavedLoginProfile>> GetSavedProfilesAsync(CancellationToken ct = default)
        => await _rememberedSessionStore.LoadAsync(ct);

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
            Tokens = (await _client.RefreshAsync(ServerUrl, Tokens.RefreshToken, ct)).Tokens;
            return true;
        }
        catch
        {
            Reset();
            return false;
        }
    }

    private void Apply(LoginResponse response, string serverUrl)
    {
        ServerUrl = serverUrl;
        Tokens = response.Tokens;
        CurrentUser = response.User;
        CurrentServer = response.Server;
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
        CurrentServer = null;
        CurrentWorkspace = null;
        CurrentSession = null;
        CurrentDevice = null;
        AssignedRole = DeviceRole.Observer;
        State = AuthSessionState.Unauthenticated;
        RaiseStateChanged();
    }

    private void RaiseStateChanged(RememberedProfileSaveResult? rememberedProfileSaveResult = null)
        => StateChanged?.Invoke(this, new AuthSessionStateChangedEventArgs(State, rememberedProfileSaveResult));
}
