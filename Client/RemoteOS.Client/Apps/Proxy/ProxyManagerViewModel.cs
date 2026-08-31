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

    public int RunningConnectionCount => Connections.Count;
    public string RuntimeVersion => Runtime?.Version ?? "—";
    public string RuntimeState => Runtime?.State.ToString() ?? "—";
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

    [RelayCommand(CanExecute = nameof(CanManage))] private Task StartProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Start);
    [RelayCommand(CanExecute = nameof(CanManage))] private Task StopProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Stop);
    [RelayCommand(CanExecute = nameof(CanManage))] private Task RestartProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Restart);
    [RelayCommand(CanExecute = nameof(CanManage))] private Task InstallRuntimeAsync() => QueueAsync(() => repository.InstallRuntimeAsync(Overview?.EngineId ?? "mihomo"));
    [RelayCommand(CanExecute = nameof(CanManage))] private Task RollbackRuntimeAsync() => QueueAsync(() => repository.RollbackRuntimeAsync());
    [RelayCommand(CanExecute = nameof(CanManage))] private Task UninstallRuntimeAsync() => QueueAsync(() => repository.UninstallRuntimeAsync());
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
        try { IsBusy = true; await operation(); StatusText = LocalizedText.Get("proxy.status.operation_queued"); }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }

    private bool CanRefresh => !IsBusy;
    private bool CanManage => canManage && !IsBusy;
    private bool CanTun => canManageTun && !IsBusy && IsTunAvailable;
    private bool CanEnableTun => CanTun && SelectedProfile is not null;
    private bool CanCreateProfile => CanManage && !IsBusy && !string.IsNullOrWhiteSpace(ProfileName);
    private bool CanManageProfile => CanManage && SelectedProfile is not null;
    private bool CanSelectGroup => CanManage && !IsBusy && SelectedGroup is not null && !string.IsNullOrWhiteSpace(SelectedProxy);
    private bool CanManageConnection => CanManage && !IsBusy && SelectedConnection is not null;

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedProfileChanged(ProxyProfileDto? value) { EnableTunCommand.NotifyCanExecuteChanged(); ActivateProfileCommand.NotifyCanExecuteChanged(); DeleteProfileCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedGroupChanged(ProxyGroupDto? value) { SelectedProxy = value?.Selected ?? string.Empty; ApplyGroupSelectionCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedProxyChanged(string value) => ApplyGroupSelectionCommand.NotifyCanExecuteChanged();
    partial void OnSelectedConnectionChanged(ProxyConnectionDto? value) => CloseConnectionCommand.NotifyCanExecuteChanged();
    partial void OnProfileNameChanged(string value) => CreateProfileCommand.NotifyCanExecuteChanged();

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged(); StartProxyCommand.NotifyCanExecuteChanged(); StopProxyCommand.NotifyCanExecuteChanged(); RestartProxyCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged(); RollbackRuntimeCommand.NotifyCanExecuteChanged(); UninstallRuntimeCommand.NotifyCanExecuteChanged();
        EnableTunCommand.NotifyCanExecuteChanged(); DisableTunCommand.NotifyCanExecuteChanged(); EmergencyDisableCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged(); ActivateProfileCommand.NotifyCanExecuteChanged(); DeleteProfileCommand.NotifyCanExecuteChanged();
        ApplyGroupSelectionCommand.NotifyCanExecuteChanged(); CloseConnectionCommand.NotifyCanExecuteChanged();
    }
    private void RaiseSummaryProperties()
    {
        OnPropertyChanged(nameof(RuntimeVersion)); OnPropertyChanged(nameof(RuntimeState)); OnPropertyChanged(nameof(HealthState)); OnPropertyChanged(nameof(TunState));
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
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
}
