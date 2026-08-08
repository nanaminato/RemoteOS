using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Client.Services;
using Client.Services.Developer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;

namespace Client.Apps.Settings.ViewModels;

/// <summary>Installed applications and the detail view for one selected application.</summary>
public sealed partial class AppsPageViewModel : SettingsPageViewModel, IDisposable
{
    private readonly ApplicationManager _apps;
    private readonly DeveloperPackageManager _packages;
    private readonly LocalizationService _localization;

    public AppsPageViewModel(ShellSettings settings, ApplicationManager apps, DeveloperPackageManager packages, LocalizationService localization)
        : base(settings, save: null)
    {
        _apps = apps;
        _packages = packages;
        _localization = localization;
        _apps.RegistryChanged += OnRegistryChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        RefreshApplications();
    }

    public override string Glyph => "📱";
    public override string DisplayNameKey => "settings.page.applications";
    public override string DisplayName => "Applications";
    public ObservableCollection<ApplicationInfo> RegisteredApps { get; } = new();

    /// <summary>Provided by Settings to open the selected application's permission page.</summary>
    public Func<ApplicationInfo, Task>? RequestPermissionEditorAsync { get; set; }
    /// <summary>Provided by Settings so an uninstall always has an explicit confirmation step.</summary>
    public Func<ApplicationInfo, Task<bool>>? RequestUninstallConfirmationAsync { get; set; }

    [ObservableProperty] private AppsSubpage _subpage = AppsSubpage.InstalledApps;
    [ObservableProperty] private ApplicationInfo? _selectedApp;
    [ObservableProperty] private string _actionStatus = string.Empty;
    [ObservableProperty] private bool _isUninstalling;

    public bool IsInstalledApps => Subpage == AppsSubpage.InstalledApps;
    public bool IsAppDetails => Subpage == AppsSubpage.AppDetails;
    public bool HasSelectedAppPermissions => SelectedApp?.Permissions.Count > 0;
    public bool HasActionStatus => !string.IsNullOrWhiteSpace(ActionStatus);
    public bool CanUninstallSelectedApp => !IsUninstalling && SelectedApp is not null
        && _packages.FindInstalled(SelectedApp.Id.Value) is not null;
    public string SelectedAppPermissionSummary => SelectedApp is null || SelectedApp.Permissions.Count == 0
        ? T("settings.apps.no_permissions", "This application does not request RemoteOS permissions.")
        : string.Format(CultureInfo.CurrentCulture,
            T("settings.apps.permissions_requested", "This application requests {0} RemoteOS permissions."),
            SelectedApp.Permissions.Count);
    public string UninstallAvailabilityText => CanUninstallSelectedApp
        ? T("settings.apps.uninstall_available", "This third-party application can be uninstalled from this device.")
        : T("settings.apps.built_in", "Built-in applications are managed by RemoteOS and cannot be uninstalled here.");

    public void Dispose()
    {
        _apps.RegistryChanged -= OnRegistryChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnRegistryChanged(object? sender, EventArgs eventArgs) => Dispatcher.UIThread.Post(RefreshApplications);
    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs eventArgs) => Dispatcher.UIThread.Post(RefreshApplications);

    private void RefreshApplications()
    {
        var apps = _apps.Registered.Select(Localize).ToArray();
        Replace(RegisteredApps, apps);
        if (SelectedApp is not null)
            SelectedApp = apps.FirstOrDefault(app => app.Id == SelectedApp.Id);
    }

    [RelayCommand]
    private void ShowInstalledApps() => Subpage = AppsSubpage.InstalledApps;

    [RelayCommand]
    private void ShowAppDetails(ApplicationInfo app)
    {
        SelectedApp = app;
        ActionStatus = string.Empty;
        Subpage = AppsSubpage.AppDetails;
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedApp))]
    private void OpenSelectedApp()
    {
        if (SelectedApp is null) return;
        ActionStatus = _apps.Launch(SelectedApp.Id)
            ? string.Format(CultureInfo.CurrentCulture, T("settings.apps.opened", "Opened {0}."), SelectedApp.DisplayName)
            : T("settings.apps.not_launchable", "This application is no longer available to launch.");
    }

    [RelayCommand]
    private async Task EditSelectedPermissionsAsync()
    {
        if (SelectedApp is not null && RequestPermissionEditorAsync is not null)
            await RequestPermissionEditorAsync(SelectedApp);
        RefreshApplications();
    }

    [RelayCommand(CanExecute = nameof(CanUninstallSelectedApp))]
    private async Task UninstallSelectedAppAsync()
    {
        if (SelectedApp is null || IsUninstalling || !CanUninstallSelectedApp) return;
        if (RequestUninstallConfirmationAsync is not null && !await RequestUninstallConfirmationAsync(SelectedApp)) return;

        IsUninstalling = true;
        try
        {
            var displayName = SelectedApp.DisplayName;
            if (await _packages.UninstallAsync(SelectedApp.Id.Value))
            {
                SelectedApp = null;
                Subpage = AppsSubpage.InstalledApps;
                ActionStatus = string.Format(CultureInfo.CurrentCulture, T("settings.apps.uninstalled", "Uninstalled {0}."), displayName);
            }
            else
                ActionStatus = T("settings.apps.already_uninstalled", "This application has already been uninstalled or does not support uninstallation.");
        }
        catch (Exception exception)
        {
            ActionStatus = string.Format(CultureInfo.CurrentCulture, T("settings.apps.uninstall_failed", "Uninstall failed: {0}"), exception.Message);
        }
        finally { IsUninstalling = false; }
    }

    private bool CanOpenSelectedApp() => SelectedApp is not null;

    partial void OnSubpageChanged(AppsSubpage value)
    {
        OnPropertyChanged(nameof(IsInstalledApps));
        OnPropertyChanged(nameof(IsAppDetails));
    }

    partial void OnSelectedAppChanged(ApplicationInfo? value)
    {
        OnPropertyChanged(nameof(HasSelectedAppPermissions));
        OnPropertyChanged(nameof(CanUninstallSelectedApp));
        OnPropertyChanged(nameof(SelectedAppPermissionSummary));
        OnPropertyChanged(nameof(UninstallAvailabilityText));
        OpenSelectedAppCommand.NotifyCanExecuteChanged();
        UninstallSelectedAppCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsUninstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUninstallSelectedApp));
        OnPropertyChanged(nameof(UninstallAvailabilityText));
        UninstallSelectedAppCommand.NotifyCanExecuteChanged();
    }

    partial void OnActionStatusChanged(string value) => OnPropertyChanged(nameof(HasActionStatus));

    private ApplicationInfo Localize(ApplicationInfo app)
    {
        var metadata = app.GetLocalizedMetadata(_localization.CurrentLanguage);
        return app with
        {
            DisplayName = T($"application.{app.Id.Value}.display_name", metadata.DisplayName),
            Description = metadata.Description is null
                ? null
                : T($"application.{app.Id.Value}.description", metadata.Description),
        };
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source) destination.Add(item);
    }
}

public enum AppsSubpage
{
    InstalledApps,
    AppDetails,
}
