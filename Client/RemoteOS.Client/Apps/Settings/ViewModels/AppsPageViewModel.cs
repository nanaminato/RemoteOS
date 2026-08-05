using System.Collections.ObjectModel;
using Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Core.Applications;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Runtime;

namespace Client.Apps.Settings.ViewModels;

/// <summary>「应用」页：① 只读列出已注册应用清单（来自 <see cref="ApplicationManager"/>）；
/// ② 默认程序映射编辑器（URI scheme / 文件扩展名 → 应用 Id），映射存到 <see cref="WorkspacePreferencesDto.DefaultApps"/>。
/// 注意：默认程序的「自动启动路由」（如点 http 链接用映射应用打开）是后续接入项；本页先完成「可设」。</summary>
public sealed partial class AppsPageViewModel : SettingsPageViewModel
{
    private readonly ApplicationManager _apps;

    public AppsPageViewModel(ShellSettings settings, ApplicationManager apps, Action? save) : base(settings, save)
    {
        _apps = apps;
        RegisteredApps = _apps.Registered;
        AvailableApps = RegisteredApps
            .Select(a => new AppOption(a.Id.Value, a.DisplayName))
            .ToList();
    }

    public override string Glyph => "📦";
    public override string DisplayName => "应用";

    /// <summary>已注册应用清单（只读）。</summary>
    public IReadOnlyList<ApplicationInfo> RegisteredApps { get; }

    /// <summary>可绑定的应用 Id 选项（id + 显示名），供默认程序下拉选择。</summary>
    public IReadOnlyList<AppOption> AvailableApps { get; }

    /// <summary>预设的 scheme / 扩展名选项。</summary>
    public static IReadOnlyList<string> AvailableSchemes { get; } = new[]
    {
        "http", "https", "mailto", "ftp",
        ".txt", ".md", ".json", ".xml", ".log",
        ".png", ".jpg", ".gif", ".pdf",
    };

    /// <summary>当前默认程序映射（可编辑）。</summary>
    public ObservableCollection<DefaultAppMappingViewModel> Mappings { get; } = new();

    [RelayCommand]
    private void AddMapping()
    {
        var preset = AvailableSchemes.FirstOrDefault(s => Mappings.All(m => !string.Equals(m.Scheme, s, StringComparison.OrdinalIgnoreCase)))
            ?? AvailableSchemes[0];
        var defaultApp = AvailableApps.FirstOrDefault().Id ?? "remoteos.browser";
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
            Mappings.Add(new DefaultAppMappingViewModel(d.Scheme, d.AppId, AvailableApps, Save, m => { Mappings.Remove(m); Save(); }));
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

    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

    partial void OnSchemeChanged(string value) => _save?.Invoke();
    partial void OnAppIdChanged(string value)
    {
        _save?.Invoke();
        OnPropertyChanged(nameof(SelectedApp));
    }
}

/// <summary>应用下拉选项（Id + 显示名）。</summary>
public sealed record AppOption(string Id, string DisplayName);
