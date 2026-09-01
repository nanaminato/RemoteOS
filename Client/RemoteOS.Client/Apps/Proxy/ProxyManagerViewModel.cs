using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Proxy;

namespace Client.Apps.Proxy;

/// <summary>Presentation state for the host-global proxy workspace. Controller details stay on the Server.</summary>
public sealed partial class ProxyManagerViewModel(IProxyRepository repository, bool canManage, bool canManageTun) : ObservableObject
{
    public ObservableCollection<ProxyProfileDto> Profiles { get; } = [];
    public ObservableCollection<ProxyGroupDto> Groups { get; } = [];
    public ObservableCollection<ProxyConnectionDto> Connections { get; } = [];
    public ObservableCollection<ProxyLogEntryDto> Logs { get; } = [];

    [ObservableProperty] private string _statusText = LocalizedText.Get("proxy.status.loading");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ProxyOverviewDto? _overview;
    [ObservableProperty] private ProxyRuntimeDto? _runtime;
    [ObservableProperty] private ProxyDnsStatusDto? _dnsStatus;
    [ObservableProperty] private ProxyProfileDto? _selectedProfile;
    [ObservableProperty] private ProxyGroupDto? _selectedGroup;
    [ObservableProperty] private ProxyConnectionDto? _selectedConnection;
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _selectedProxy = string.Empty;

    public Func<Task<string?>>? RequestServerRuntimePackageAsync { get; set; }

    public void SetServerRuntimePackageRequest(Func<Task<string?>>? request)
    {
        RequestServerRuntimePackageAsync = request;
        InstallRuntimeFromServerFileCommand.NotifyCanExecuteChanged();
    }

    public int RunningConnectionCount => Connections.Count;
    public string RuntimeVersion => Runtime?.Version ?? "—";
    public string RuntimeState => Runtime?.State.ToString() ?? "—";
    public bool ProxyIsRunning => Runtime?.State == ProxyRuntimeState.Running;
    public bool ProxyCanToggle => Runtime?.State is ProxyRuntimeState.Stopped or ProxyRuntimeState.Running;
    public string ProxyActionText => LocalizedText.Get(ProxyIsRunning ? "proxy.stop" : "proxy.start");
    public string ProxyStateLabel => Runtime?.State switch
    {
        ProxyRuntimeState.Running => LocalizedText.Get("proxy.runtime_running"),
        ProxyRuntimeState.Stopped => LocalizedText.Get("proxy.runtime_stopped"),
        _ => LocalizedText.Get("proxy.runtime_unavailable"),
    };
    public bool RuntimeIsInstalled => Runtime is { Mode: ProxyRuntimeMode.Managed, IntegrityVerified: true };
    public bool RuntimeIsNotInstalled => !RuntimeIsInstalled;
    public string RuntimeInstalledVersion => Runtime?.Version ?? "—";
    public string RuntimeAvailableVersion => LocalizedText.Format("proxy.runtime.available_version", RuntimeInstalledVersion);
    public string HealthState => Overview?.Health.State.ToString() ?? "—";
    public string TunState => Overview?.Health.TunState.ToString() ?? "—";
    public string DnsState => DnsStatus is { Enabled: true }
        ? (DnsStatus.HijackEnabled ? LocalizedText.Get("proxy.dns.hijack_enabled") : LocalizedText.Get("proxy.dns.enabled"))
        : LocalizedText.Get("proxy.dns.disabled");
    public string ActiveProfileName => Overview?.ActiveProfile?.Name ?? LocalizedText.Get("proxy.none");
    public bool IsTunAvailable => Overview?.PlatformCapabilities.SupportsTun == true;
    public bool IsRecoveryRequired => Overview?.Recovery.RecoveryRequired == true;

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var overviewTask = repository.GetOverviewAsync();
            var profilesTask = repository.ListProfilesAsync();
            await Task.WhenAll(overviewTask, profilesTask);

            Overview = await overviewTask;
            Runtime = Overview.Runtime;
            Replace(Profiles, await profilesTask);
            // A stopped/not-yet-installed runtime cannot answer controller requests. The shell
            // must still show its install and profile pages rather than fail the entire refresh.
            await Task.WhenAll(
                LoadOptionalAsync(() => repository.ListGroupsAsync(), values => Replace(Groups, values)),
                LoadOptionalAsync(() => repository.ListConnectionsAsync(), values => Replace(Connections, values)),
                LoadOptionalAsync(() => repository.ListLogsAsync(), values => Replace(Logs, values)),
                LoadOptionalAsync(() => repository.GetDnsStatusAsync(), value => DnsStatus = value));
            StatusText = Overview.Recovery.RecoveryRequired
                ? LocalizedText.Get("proxy.status.recovery_required")
                : LocalizedText.Format("proxy.status.ready", Overview.Runtime.State, Overview.Health.State);
            RaiseSummaryProperties();
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanStartProxy))] private Task StartProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Start);
    [RelayCommand(CanExecute = nameof(CanStopProxy))] private Task StopProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Stop);
    [RelayCommand(CanExecute = nameof(CanRestartProxy))] private Task RestartProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Restart);
    [RelayCommand(CanExecute = nameof(CanToggleProxy))]
    private Task ToggleProxyAsync() => LifecycleAsync(ProxyIsRunning ? ProxyLifecycleAction.Stop : ProxyLifecycleAction.Start);
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))] private Task InstallRuntimeAsync() => QueueAsync(() => repository.InstallRuntimeAsync(Overview?.EngineId ?? "mihomo"));
    [RelayCommand(CanExecute = nameof(CanInstallRuntimeFromServerFile))]
    private async Task InstallRuntimeFromServerFileAsync()
    {
        if (RequestServerRuntimePackageAsync is not { } requestPackage) return;
        var archivePath = await requestPackage();
        if (!string.IsNullOrWhiteSpace(archivePath))
            await QueueAsync(() => repository.InstallRuntimeFromServerFileAsync(Overview?.EngineId ?? "mihomo", archivePath));
    }
    [RelayCommand(CanExecute = nameof(CanInstalledRuntime))] private Task RollbackRuntimeAsync() => QueueAsync(() => repository.RollbackRuntimeAsync());
    [RelayCommand(CanExecute = nameof(CanInstalledRuntime))] private Task UninstallRuntimeAsync() => QueueAsync(() => repository.UninstallRuntimeAsync());
    [RelayCommand(CanExecute = nameof(CanEnableTun))] private Task EnableTunAsync() => QueueAsync(() => repository.EnableTunAsync(SelectedProfile!.Id));
    [RelayCommand(CanExecute = nameof(CanTun))] private Task DisableTunAsync() => QueueAsync(() => repository.DisableTunAsync());
    [RelayCommand(CanExecute = nameof(CanTun))] private Task EmergencyDisableAsync() => QueueAsync(() => repository.EmergencyDisableTunAsync());

    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private async Task CreateProfileAsync()
    {
        var name = ProfileName.Trim();
        try
        {
            IsBusy = true;
            var profile = await repository.CreateProfileAsync(name, Overview?.EngineId ?? "mihomo");
            Profiles.Add(profile);
            ProfileName = string.Empty;
            StatusText = LocalizedText.Format("proxy.status.profile_created", profile.Name);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManageProfile))]
    private async Task ActivateProfileAsync()
    {
        var profile = SelectedProfile!;
        try
        {
            IsBusy = true;
            var activated = await repository.ActivateProfileAsync(profile.Id);
            Overview = Overview is null ? null : Overview with { ActiveProfile = activated };
            for (var index = 0; index < Profiles.Count; index++) Profiles[index] = Profiles[index] with { IsActive = Profiles[index].Id == activated.Id };
            SelectedProfile = activated;
            StatusText = LocalizedText.Format("proxy.status.profile_activated", activated.Name);
            RaiseSummaryProperties();
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManageProfile))]
    private async Task DeleteProfileAsync()
    {
        var profile = SelectedProfile!;
        try
        {
            IsBusy = true;
            await repository.DeleteProfileAsync(profile.Id);
            Profiles.Remove(profile);
            SelectedProfile = null;
            StatusText = LocalizedText.Format("proxy.status.profile_deleted", profile.Name);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSelectGroup))]
    private async Task ApplyGroupSelectionAsync()
    {
        try
        {
            IsBusy = true;
            await repository.SelectGroupAsync(SelectedGroup!.Name, SelectedProxy);
            var index = Groups.IndexOf(SelectedGroup);
            if (index >= 0) Groups[index] = SelectedGroup with { Selected = SelectedProxy };
            StatusText = LocalizedText.Format("proxy.status.node_selected", SelectedProxy);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManageConnection))]
    private async Task CloseConnectionAsync()
    {
        var connection = SelectedConnection!;
        try
        {
            IsBusy = true;
            await repository.CloseConnectionAsync(connection.Id);
            Connections.Remove(connection);
            SelectedConnection = null;
            OnPropertyChanged(nameof(RunningConnectionCount));
            StatusText = LocalizedText.Get("proxy.status.connection_closed");
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }

    private async Task LifecycleAsync(ProxyLifecycleAction action) => await QueueAsync(() => repository.LifecycleAsync(action));
    private async Task QueueAsync(Func<Task<ProxyOperationAcceptedDto>> operation)
    {
        try
        {
            IsBusy = true;
            var accepted = await operation();
            await TrackOperationAsync(accepted.OperationId);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }

    private async Task TrackOperationAsync(Guid operationId)
    {
        while (true)
        {
            var operation = await repository.GetOperationAsync(operationId);
            if (operation is null) { StatusText = LocalizedText.Get("proxy.status.operation_unavailable"); return; }
            StatusText = FormatOperation(operation);
            if (operation.State is ProxyOperationState.Succeeded or ProxyOperationState.Failed or ProxyOperationState.Cancelled or ProxyOperationState.Interrupted)
            {
                if (operation.State == ProxyOperationState.Succeeded) await RefreshAsync();
                else await LoadLogsAsync();
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private bool CanRefresh => !IsBusy;
    private bool CanManage => canManage && !IsBusy;
    private bool CanInstallRuntime => CanManage && RuntimeIsNotInstalled;
    private bool CanInstalledRuntime => CanManage && RuntimeIsInstalled;
    private bool CanStartProxy => CanManage && Runtime?.State == ProxyRuntimeState.Stopped;
    private bool CanStopProxy => CanManage && Runtime?.State == ProxyRuntimeState.Running;
    private bool CanRestartProxy => CanManage && Runtime?.State is ProxyRuntimeState.Stopped or ProxyRuntimeState.Running;
    private bool CanToggleProxy => CanManage && ProxyCanToggle;
    private bool CanInstallRuntimeFromServerFile => CanInstallRuntime && RequestServerRuntimePackageAsync is not null;
    private bool CanTun => canManageTun && !IsBusy && IsTunAvailable;
    private bool CanEnableTun => CanTun && SelectedProfile is not null;
    private bool CanCreateProfile => CanManage && !IsBusy && !string.IsNullOrWhiteSpace(ProfileName);
    private bool CanManageProfile => CanManage && SelectedProfile is not null;
    private bool CanSelectGroup => CanManage && !IsBusy && SelectedGroup is not null && !string.IsNullOrWhiteSpace(SelectedProxy);
    private bool CanManageConnection => CanManage && !IsBusy && SelectedConnection is not null;

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedProfileChanged(ProxyProfileDto? value) { EnableTunCommand.NotifyCanExecuteChanged(); ActivateProfileCommand.NotifyCanExecuteChanged(); DeleteProfileCommand.NotifyCanExecuteChanged(); }
    partial void OnRuntimeChanged(ProxyRuntimeDto? value)
    {
        OnPropertyChanged(nameof(ProxyIsRunning)); OnPropertyChanged(nameof(ProxyCanToggle));
        OnPropertyChanged(nameof(ProxyActionText)); OnPropertyChanged(nameof(ProxyStateLabel));
        OnPropertyChanged(nameof(RuntimeIsInstalled)); OnPropertyChanged(nameof(RuntimeIsNotInstalled));
        OnPropertyChanged(nameof(RuntimeInstalledVersion)); OnPropertyChanged(nameof(RuntimeAvailableVersion));
        StartProxyCommand.NotifyCanExecuteChanged(); StopProxyCommand.NotifyCanExecuteChanged(); RestartProxyCommand.NotifyCanExecuteChanged();
        ToggleProxyCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged(); InstallRuntimeFromServerFileCommand.NotifyCanExecuteChanged();
        RollbackRuntimeCommand.NotifyCanExecuteChanged(); UninstallRuntimeCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedGroupChanged(ProxyGroupDto? value) { SelectedProxy = value?.Selected ?? string.Empty; ApplyGroupSelectionCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedProxyChanged(string value) => ApplyGroupSelectionCommand.NotifyCanExecuteChanged();
    partial void OnSelectedConnectionChanged(ProxyConnectionDto? value) => CloseConnectionCommand.NotifyCanExecuteChanged();
    partial void OnProfileNameChanged(string value) => CreateProfileCommand.NotifyCanExecuteChanged();

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged(); StartProxyCommand.NotifyCanExecuteChanged(); StopProxyCommand.NotifyCanExecuteChanged(); RestartProxyCommand.NotifyCanExecuteChanged(); ToggleProxyCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged(); InstallRuntimeFromServerFileCommand.NotifyCanExecuteChanged(); RollbackRuntimeCommand.NotifyCanExecuteChanged(); UninstallRuntimeCommand.NotifyCanExecuteChanged();
        EnableTunCommand.NotifyCanExecuteChanged(); DisableTunCommand.NotifyCanExecuteChanged(); EmergencyDisableCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged(); ActivateProfileCommand.NotifyCanExecuteChanged(); DeleteProfileCommand.NotifyCanExecuteChanged();
        ApplyGroupSelectionCommand.NotifyCanExecuteChanged(); CloseConnectionCommand.NotifyCanExecuteChanged();
    }
    private void RaiseSummaryProperties()
    {
        OnPropertyChanged(nameof(RuntimeVersion)); OnPropertyChanged(nameof(RuntimeState)); OnPropertyChanged(nameof(RuntimeInstalledVersion)); OnPropertyChanged(nameof(RuntimeAvailableVersion));
        OnPropertyChanged(nameof(HealthState)); OnPropertyChanged(nameof(TunState));
        OnPropertyChanged(nameof(DnsState)); OnPropertyChanged(nameof(ActiveProfileName)); OnPropertyChanged(nameof(IsTunAvailable)); OnPropertyChanged(nameof(IsRecoveryRequired)); OnPropertyChanged(nameof(RunningConnectionCount));
        EnableTunCommand.NotifyCanExecuteChanged(); DisableTunCommand.NotifyCanExecuteChanged(); EmergencyDisableCommand.NotifyCanExecuteChanged();
    }
    private static async Task LoadOptionalAsync<T>(Func<Task<T>> load, Action<T> apply)
    {
        try { apply(await load()); }
        // Controller-specific pages simply remain empty until a runtime is healthy.
        catch (ProxyRequestException) { }
        catch (HttpRequestException) { }
    }
    private async Task LoadLogsAsync()
    {
        try { Replace(Logs, await repository.ListLogsAsync()); }
        catch (ProxyRequestException) { }
        catch (HttpRequestException) { }
    }
    private static string FormatOperation(ProxyOperationDto operation)
    {
        if (operation.State is ProxyOperationState.Failed or ProxyOperationState.Interrupted)
            return LocalizedText.Format("proxy.status.failed", operation.ProblemCode);
        var key = "proxy.operation." + operation.Stage;
        var stage = LocalizedText.Get(key);
        return stage == key ? operation.Stage : stage;
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
}
