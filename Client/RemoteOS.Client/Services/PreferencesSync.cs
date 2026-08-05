using Client.Apps.Settings;
using Client.Services.Auth;
using RemoteOS.Protocol.Workspace;

namespace Client.Services;

/// <summary>用户偏好同步（单例）。监听 <see cref="IAuthSession.StateChanged"/>：
/// 认证成功 → 从服务端拉取 <see cref="WorkspacePreferencesDto"/>，应用到 <see cref="ShellSettings"/>
/// （壁纸/主题/时间格式/语言/区域，桌面外壳即时生效）并填充 <see cref="DefaultAppRegistry"/>；
/// 登出 → 重置为默认偏好。设置应用编辑后的保存由 <c>SettingsViewModel</c> 自行处理。</summary>
public sealed class PreferencesSync : IDisposable
{
    private readonly IAuthSession _session;
    private readonly ISettingsClient _client;
    private readonly ShellSettings _settings;
    private readonly DefaultAppRegistry _registry;

    public PreferencesSync(IAuthSession session, ISettingsClient client, ShellSettings settings, DefaultAppRegistry registry)
    {
        _session = session;
        _client = client;
        _settings = settings;
        _registry = registry;
        _session.StateChanged += OnStateChanged;
        // 桌面外壳可能在登录后才构造本服务——若此时已认证，立即加载。
        _ = LoadIfAuthenticatedAsync();
    }

    private void OnStateChanged(object? sender, AuthSessionStateChangedEventArgs e)
    {
        if (e.State == AuthSessionState.Authenticated)
            _ = LoadIfAuthenticatedAsync();
        else if (e.State == AuthSessionState.Unauthenticated)
        {
            _settings.Apply(WorkspacePreferencesDto.Default);
            _registry.SetMappings(WorkspacePreferencesDto.Default.DefaultApps);
        }
    }

    private async Task LoadIfAuthenticatedAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } ws })
            return;
        try
        {
            var prefs = await _client.GetAsync(url, tokens.AccessToken, ws.Id);
            _settings.Apply(prefs);
            _registry.SetMappings(prefs.DefaultApps);
        }
        catch
        {
            // 服务端无偏好或旧版本：沿用 ShellSettings 默认值。
        }
    }

    public void Dispose() => _session.StateChanged -= OnStateChanged;
}
