using Client.Services;
using Client.Services.Auth;
using Client.Services.AppPermissions;
using Client.Services.Developer;
using Client.Apps.TaskManager;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Runtime;

namespace Client.Apps.Settings.ViewModels;

/// <summary>设置应用根 VM。左侧导航（5 个分类页）+ 右侧内容（当前选中页）。
/// 透传编辑 <see cref="ShellSettings"/>（即时反映到桌面外壳），并由 <see cref="Save"/> 触发防抖保存到服务端
/// （<c>/workspaces/{id}/preferences</c>，与 TerminalSettings/BrowserSettings 同模式）。
/// <see cref="InitializeAsync"/> 在窗口打开后调用一次：从服务端拉取偏好应用到 ShellSettings + 填充默认程序映射。</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ShellSettings _settings;
    private readonly ISettingsClient _client;
    private readonly IAuthSession _session;
    private readonly ApplicationManager? _apps;
    private readonly IRemoteOsClient? _remote;
    private readonly ITaskManagerClient? _system;
    private readonly DefaultAppRegistry? _registry;
    private readonly IAppPermissionManager? _permissions;
    private readonly DeveloperModeService? _developerMode;
    private CancellationTokenSource? _saveCts;
    private bool _initialized;

    public SettingsViewModel(
        ShellSettings settings,
        ISettingsClient client,
        IAuthSession session,
        ApplicationManager? apps,
        IRemoteOsClient? remote,
        ITaskManagerClient? system,
        DefaultAppRegistry? registry,
        IAppPermissionManager? permissions,
        DeveloperModeService? developerMode)
    {
        _settings = settings;
        _client = client;
        _session = session;
        _apps = apps;
        _remote = remote;
        _system = system;
        _registry = registry;
        _permissions = permissions;
        _developerMode = developerMode;

        var save = (Action)Save;
        Pages = new SettingsPageViewModel[]
        {
            new SystemPageViewModel(settings, session, save),
            new PersonalizationPageViewModel(settings, save),
            new TimeLanguagePageViewModel(settings, save),
            new NetworkPageViewModel(settings, session, remote!, system!, save),
            new AppsPageViewModel(settings, apps!, permissions!, developerMode!, save),
        };
        _selectedPage = Pages[0];
    }

    public ShellSettings Settings => _settings;
    public IReadOnlyList<SettingsPageViewModel> Pages { get; }

    [ObservableProperty] private SettingsPageViewModel? _selectedPage;

    /// <summary>Host navigation entry point used when an application sends the user to Settings.</summary>
    public void SelectApplicationsPage() =>
        SelectedPage = Pages.OfType<AppsPageViewModel>().FirstOrDefault() ?? SelectedPage;

    /// <summary>窗口打开后调用：加载服务端偏好并应用到 ShellSettings + 默认程序映射。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } ws })
            return;

        if (Pages.OfType<NetworkPageViewModel>().FirstOrDefault() is { } networkPage)
            await networkPage.LoadServerAddressesAsync();

        try
        {
            var prefs = await _client.GetAsync(url, tokens.AccessToken, ws.Id);
            _settings.Apply(prefs);
            if (Pages.FirstOrDefault(p => p is AppsPageViewModel) is AppsPageViewModel appsPage)
                appsPage.SetMappings(prefs.DefaultApps);
        }
        catch
        {
            // 服务端无偏好或旧版本：沿用 ShellSettings 默认值，设置仍可用（仅本地，不持久化）。
        }
    }

    /// <summary>页 VM 编辑后调用：防抖 300ms 后保存到服务端。</summary>
    internal void Save()
    {
        if (!_initialized) return; // 初始化 Apply 期间不保存
        // 即时同步默认程序映射到全局注册表（启动路由可立即读到最新意图）。
        _registry?.SetMappings(Pages.OfType<AppsPageViewModel>().FirstOrDefault()?.ToMappings());
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        _ = SaveAsync(_saveCts.Token);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await Task.Delay(300, ct); }
        catch (OperationCanceledException) { return; }

        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } ws })
            return;

        var mappings = Pages.OfType<AppsPageViewModel>().FirstOrDefault()?.ToMappings() ?? Array.Empty<DefaultAppMappingDto>();
        var prefs = _settings.ToPreferences(mappings);
        try { await _client.SaveAsync(url, tokens.AccessToken, ws.Id, prefs, ct); }
        catch { /* 保留本地值，后续改动可重试 */ }
    }

    public void Dispose()
    {
        _saveCts?.Cancel();
        foreach (var page in Pages.OfType<IDisposable>())
            page.Dispose();
    }
}
