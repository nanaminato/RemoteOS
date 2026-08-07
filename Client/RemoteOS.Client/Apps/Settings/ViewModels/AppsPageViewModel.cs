using System.Collections.ObjectModel;
using Avalonia.Threading;
using Client.Services;
using Client.Services.AppPermissions;
using Client.Services.Developer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Core.Applications;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Runtime;

namespace Client.Apps.Settings.ViewModels;

/// <summary>「应用」页：① 只读列出已注册应用清单（来自 <see cref="ApplicationManager"/>）；
/// ② 默认程序映射编辑器（URI scheme / 文件扩展名 → 应用 Id），映射存到 <see cref="WorkspacePreferencesDto.DefaultApps"/>。
/// 注意：默认程序的「自动启动路由」（如点 http 链接用映射应用打开）是后续接入项；本页先完成「可设」。</summary>
public sealed partial class AppsPageViewModel : SettingsPageViewModel, IDisposable
{
    private readonly ApplicationManager _apps;
    private readonly IAppPermissionManager _permissions;

    public AppsPageViewModel(
        ShellSettings settings,
        ApplicationManager apps,
        IAppPermissionManager permissions,
        DeveloperModeService developerMode,
        Action? save) : base(settings, save)
    {
        _apps = apps;
        _permissions = permissions;
        DeveloperMode = new DeveloperModeViewModel(developerMode);
        _apps.RegistryChanged += OnRegistryChanged;
        RefreshApplications();
    }

    public override string Glyph => "📦";
    public override string DisplayName => "应用";

    /// <summary>已注册应用清单（只读）。</summary>
    public ObservableCollection<ApplicationInfo> RegisteredApps { get; } = new();

    /// <summary>可绑定的应用 Id 选项（id + 显示名），供默认程序下拉选择。</summary>
    public ObservableCollection<AppOption> AvailableApps { get; } = new();

    /// <summary>Applications with manifest-declared host capabilities that the user can grant or revoke.</summary>
    public ObservableCollection<AppPermissionAppViewModel> PermissionApps { get; } = new();
    public bool HasPermissionRequests => PermissionApps.Count > 0;
    public DeveloperModeViewModel DeveloperMode { get; }

    /// <summary>Provided by the Settings window to open an app-specific permission editor.</summary>
    public Func<AppPermissionAppViewModel, Task>? RequestPermissionEditorAsync { get; set; }

    /// <summary>URI schemes plus every extension declared by an installed application.</summary>
    public ObservableCollection<string> AvailableSchemes { get; } = new();

    /// <summary>当前默认程序映射（可编辑）。</summary>
    public ObservableCollection<DefaultAppMappingViewModel> Mappings { get; } = new();

    public void Dispose() => _apps.RegistryChanged -= OnRegistryChanged;

    private void OnRegistryChanged(object? sender, EventArgs eventArgs)
        => Dispatcher.UIThread.Post(RefreshApplications);

    private void RefreshApplications()
    {
        var apps = _apps.Registered;
        Replace(RegisteredApps, apps);
        Replace(AvailableApps, apps.Select(app => new AppOption(app.Id.Value, app.DisplayName, app.FileExtensions)));
        Replace(AvailableSchemes, new[] { "http", "https", "mailto", "ftp" }
            .Concat(apps.SelectMany(app => app.FileExtensions))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scheme => scheme.StartsWith('.') ? 1 : 0)
            .ThenBy(scheme => scheme, StringComparer.OrdinalIgnoreCase));
        Replace(PermissionApps, apps
            .Where(app => app.Permissions.Count > 0)
            .Select(app => new AppPermissionAppViewModel(app, _permissions)));
        foreach (var mapping in Mappings)
            mapping.NotifyAvailableAppsChanged();
        OnPropertyChanged(nameof(HasPermissionRequests));
    }

    [RelayCommand]
    private async Task EditPermissionsAsync(AppPermissionAppViewModel app)
    {
        if (RequestPermissionEditorAsync is not null)
            await RequestPermissionEditorAsync(app);
        RefreshApplications();
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source)
            destination.Add(item);
    }

    [RelayCommand]
    private void AddMapping()
    {
        var preset = AvailableSchemes.FirstOrDefault(s => Mappings.All(m => !string.Equals(m.Scheme, s, StringComparison.OrdinalIgnoreCase)))
            ?? AvailableSchemes[0];
        var defaultApp = AvailableApps.FirstOrDefault()?.Id ?? "remoteos.browser";
        Mappings.Add(new DefaultAppMappingViewModel(preset, defaultApp, AvailableApps, Save, m => { Mappings.Remove(m); Save(); }));
        Save();
    }

    [RelayCommand]
    private void RemoveMapping(DefaultAppMappingViewModel mapping)
    {
        if (Mappings.Remove(mapping))
            Save();
    }

    /// <summary>从服务端 DTO 填充映射（初始化时由根 VM 调用）。</summary>
    public void SetMappings(IEnumerable<DefaultAppMappingDto>? dtos)
    {
        Mappings.Clear();
        if (dtos is null) return;
        foreach (var d in dtos)
        {
            if (AvailableSchemes.All(scheme => !string.Equals(scheme, d.Scheme, StringComparison.OrdinalIgnoreCase)))
                AvailableSchemes.Add(d.Scheme);
            Mappings.Add(new DefaultAppMappingViewModel(d.Scheme, d.AppId, AvailableApps, Save, m => { Mappings.Remove(m); Save(); }));
        }
    }

    /// <summary>导出当前映射为服务端 DTO（保存时由根 VM 调用）。</summary>
    public IReadOnlyList<DefaultAppMappingDto> ToMappings()
        => Mappings.Select(m => new DefaultAppMappingDto(m.Scheme, m.AppId)).ToArray();
}

/// <summary>一条可编辑的默认程序映射（scheme/ext → appId）。</summary>
public sealed partial class DefaultAppMappingViewModel : ObservableObject
{
    private readonly Action? _save;
    private readonly Action<DefaultAppMappingViewModel>? _remove;
    public IReadOnlyList<AppOption> AvailableApps { get; }

    public DefaultAppMappingViewModel(string scheme, string appId, IReadOnlyList<AppOption> availableApps,
        Action? save, Action<DefaultAppMappingViewModel>? remove)
    {
        _save = save;
        _remove = remove;
        AvailableApps = availableApps;
        _scheme = scheme;
        _appId = appId;
    }

    [ObservableProperty] private string _scheme;
    [ObservableProperty] private string _appId;

    /// <summary>当前选中的应用选项（供 ComboBox SelectedItem 绑定，与 <see cref="AppId"/> 双向同步）。</summary>
    public AppOption? SelectedApp
    {
        get => AvailableApps.FirstOrDefault(a => string.Equals(a.Id, AppId, StringComparison.Ordinal));
        set { if (value is not null) AppId = value.Id; }
    }

    /// <summary>Applications compatible with the mapped file extension; URI schemes remain unrestricted.</summary>
    public IReadOnlyList<AppOption> CompatibleApps => Scheme.StartsWith(".", StringComparison.Ordinal)
        ? AvailableApps.Where(app => app.SupportedFileExtensions.Contains(Scheme, StringComparer.OrdinalIgnoreCase)).ToArray()
        : AvailableApps;

    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

    partial void OnSchemeChanged(string value)
    {
        _save?.Invoke();
        OnPropertyChanged(nameof(CompatibleApps));
        OnPropertyChanged(nameof(SelectedApp));
    }
    partial void OnAppIdChanged(string value)
    {
        _save?.Invoke();
        OnPropertyChanged(nameof(SelectedApp));
    }

    public void NotifyAvailableAppsChanged()
    {
        OnPropertyChanged(nameof(CompatibleApps));
        OnPropertyChanged(nameof(SelectedApp));
    }
}

/// <summary>应用下拉选项（Id + 显示名）。</summary>
public sealed record AppOption(string Id, string DisplayName, IReadOnlyList<string> SupportedFileExtensions);

/// <summary>An application whose manifest declares host capabilities.</summary>
public sealed class AppPermissionAppViewModel
{
    public AppPermissionAppViewModel(ApplicationInfo app, IAppPermissionManager permissions)
    {
        App = app;
        DisplayName = app.DisplayName;
        RequestedPermissionCount = app.Permissions.Count;
        GrantedPermissionCount = app.Permissions.Count(permission => permissions.IsGranted(app.Id, permission));
    }

    public ApplicationInfo App { get; }
    public string DisplayName { get; }
    public int RequestedPermissionCount { get; }
    public int GrantedPermissionCount { get; }
    public string GrantSummary => GrantedPermissionCount == 0
        ? "未授予权限"
        : $"已授予 {GrantedPermissionCount} / {RequestedPermissionCount} 项权限";
}

/// <summary>Settings-facing wrapper around the local Developer Mode switch and pairing secret.</summary>
public sealed partial class DeveloperModeViewModel : ObservableObject
{
    private readonly DeveloperModeService _developerMode;

    public DeveloperModeViewModel(DeveloperModeService developerMode)
    {
        _developerMode = developerMode;
        _isEnabled = developerMode.IsEnabled;
    }

    [ObservableProperty] private bool _isEnabled;

    public string Endpoint => _developerMode.Endpoint;
    public string PairingToken => _developerMode.PairingToken;

    partial void OnIsEnabledChanged(bool value) => _developerMode.SetEnabled(value);

    [RelayCommand]
    private void RegeneratePairingToken()
    {
        _developerMode.RegeneratePairingToken();
        OnPropertyChanged(nameof(PairingToken));
    }
}
