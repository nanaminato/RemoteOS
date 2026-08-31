using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Proxy;

namespace Client.Apps.Proxy;

/// <summary>Window-local, engine-neutral Proxy presentation state.</summary>
public sealed partial class ProxyManagerViewModel(IProxyRepository repository, bool canManage, bool canManageTun) : ObservableObject
{
    public ObservableCollection<ProxyProfileDto> Profiles { get; } = [];
    [ObservableProperty] private string _statusText = LocalizedText.Get("proxy.status.loading");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ProxyOverviewDto? _overview;

    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Overview = await repository.GetOverviewAsync(); Profiles.Clear();
            foreach (var profile in await repository.ListProfilesAsync()) Profiles.Add(profile);
            StatusText = Overview.Recovery.RecoveryRequired ? LocalizedText.Get("proxy.status.recovery_required")
                : LocalizedText.Format("proxy.status.ready", Overview.Runtime.State, Overview.Health.State);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanManage))] private Task StartProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Start);
    [RelayCommand(CanExecute = nameof(CanManage))] private Task StopProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Stop);
    [RelayCommand(CanExecute = nameof(CanManage))] private Task RestartProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Restart);
    [RelayCommand(CanExecute = nameof(CanTun))] private async Task EmergencyDisableAsync()
    {
        try { await repository.EmergencyDisableTunAsync(); StatusText = LocalizedText.Get("proxy.status.operation_queued"); }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
    }
    private async Task LifecycleAsync(ProxyLifecycleAction action)
    {
        try { await repository.LifecycleAsync(action); StatusText = LocalizedText.Get("proxy.status.operation_queued"); }
        catch (Exception exception) { StatusText = LocalizedText.Format("proxy.status.failed", exception.Message); }
    }
    private bool CanRefresh => !IsBusy;
    private bool CanManage => canManage && !IsBusy;
    private bool CanTun => canManageTun && !IsBusy;
}
