using Client.Services;
using Client.Services.Developer;
using Client.Localization;
using RemoteOS.AppSDK;
using RemoteOS.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps.Settings.ViewModels;

/// <summary>Developer features are a top-level Settings category, separate from Applications.</summary>
public sealed class DeveloperPageViewModel : SettingsPageViewModel
{
    public DeveloperPageViewModel(ShellSettings settings, DeveloperModeService developerMode,
        DeveloperPackageManager packages, ApplicationManager applications, LocalizationService localization, Action? save)
        : base(settings, save)
    {
        DeveloperMode = new DeveloperModeViewModel(developerMode);
        NetworkInspector = new NetworkInspectorLauncherViewModel(developerMode, packages, applications, localization);
    }

    public override string Glyph => "🛠️";
    public override string DisplayNameKey => "settings.page.developer";
    public override string DisplayName => "Developer";
    public DeveloperModeViewModel DeveloperMode { get; }
    public NetworkInspectorLauncherViewModel NetworkInspector { get; }
}

public sealed partial class NetworkInspectorLauncherViewModel : ObservableObject
{
    private readonly DeveloperModeService _developerMode;
    private readonly DeveloperPackageManager _packages;
    private readonly ApplicationManager _applications;

    public NetworkInspectorLauncherViewModel(DeveloperModeService developerMode, DeveloperPackageManager packages,
        ApplicationManager applications, LocalizationService localization)
    {
        _developerMode = developerMode;
        _packages = packages;
        _applications = applications;
        _developerMode.Changed += (_, _) => Refresh();
        _applications.RegistryChanged += (_, _) => Refresh();
        localization.LanguageChanged += (_, _) => Refresh();
    }

    public bool IsInstalled => _packages.FindInstalled(NetworkDiagnosticsApplication.InspectorAppId) is not null;
    public bool CanOpen => _developerMode.IsEnabled && IsInstalled;
    public string Status => !_developerMode.IsEnabled ? LocalizedText.Get("settings.network_inspector.requires_developer_mode")
        : IsInstalled ? LocalizedText.Get("settings.network_inspector.installed") : LocalizedText.Get("settings.network_inspector.not_installed");

    [RelayCommand]
    private void Open() => _applications.Launch(new RemoteOS.Core.Applications.AppId(NetworkDiagnosticsApplication.InspectorAppId));

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(Status));
        OpenCommand.NotifyCanExecuteChanged();
    }
}

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
