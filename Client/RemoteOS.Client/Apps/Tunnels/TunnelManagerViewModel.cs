using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Tunnels;

namespace Client.Apps.Tunnels;

/// <summary>Read state is refreshed as a snapshot; closing the window does not alter server-owned frpc processes.</summary>
public sealed partial class TunnelManagerViewModel(IRemoteTunnelClient client, bool canManage) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    public ObservableCollection<TunnelServerProfileDto> Profiles { get; } = [];
    public ObservableCollection<TunnelDefinitionDto> Tunnels { get; } = [];
    [ObservableProperty] private TunnelServerProfileDto? _selectedProfile;
    [ObservableProperty] private string _runtimeText = "—";
    [ObservableProperty] private string _statusText = LocalizedText.Get("tunnels.status.loading");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _logsText = string.Empty;
    [ObservableProperty] private string _runtimeVersion = "v0.71.0";
    public bool HasSelectedProfile => SelectedProfile is not null;
    public bool CanManage => canManage;
    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return; IsBusy = true;
        try
        {
            var profiles = client.ListProfilesAsync(_lifetime.Token); var tunnels = client.ListAsync(_lifetime.Token); var runtime = client.GetRuntimeAsync(_lifetime.Token);
            await Task.WhenAll(profiles, tunnels, runtime);
            Replace(Profiles, await profiles); Replace(Tunnels, await tunnels);
            var info = await runtime; RuntimeText = $"{info.State} {info.Version ?? string.Empty}".Trim();
            StatusText = LocalizedText.Get("tunnels.status.ready");
        }
        catch (OperationCanceledException) { }
        catch { StatusText = LocalizedText.Get("tunnels.status.failed"); }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanApplySelected))]
    private async Task ApplySelectedAsync() => await RunOperationAsync(selected => client.ApplyAsync(selected.Id, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanApplySelected))]
    private async Task StopSelectedAsync() => await RunOperationAsync(selected => client.StopAsync(selected.Id, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private async Task LoadLogsAsync()
    {
        if (SelectedProfile is null) return;
        try { LogsText = string.Join(Environment.NewLine, (await client.GetLogsAsync(SelectedProfile.Id, _lifetime.Token)).Select(log => $"{log.Timestamp:HH:mm:ss} {log.Level}: {log.Message}")); }
        catch { StatusText = LocalizedText.Get("tunnels.status.failed"); }
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task InstallRuntimeAsync() => await RunRuntimeOperationAsync(() => client.InstallManagedRuntimeAsync(RuntimeVersion, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RollbackRuntimeAsync() => await RunRuntimeOperationAsync(() => client.RollbackManagedRuntimeAsync(_lifetime.Token));
    private bool CanApplySelected => CanManage && HasSelectedProfile;
    partial void OnSelectedProfileChanged(TunnelServerProfileDto? value) { OnPropertyChanged(nameof(HasSelectedProfile)); ApplySelectedCommand.NotifyCanExecuteChanged(); StopSelectedCommand.NotifyCanExecuteChanged(); LoadLogsCommand.NotifyCanExecuteChanged(); }
    private async Task RunOperationAsync(Func<TunnelServerProfileDto, Task<TunnelOperationResultDto>> operation)
    {
        if (IsBusy || SelectedProfile is null) return; IsBusy = true;
        try { var result = await operation(SelectedProfile); StatusText = result.Succeeded ? result.State.ToString() : result.ProblemCode; }
        catch { StatusText = LocalizedText.Get("tunnels.status.failed"); }
        finally { IsBusy = false; }
    }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { collection.Clear(); foreach (var value in values) collection.Add(value); }
    private async Task RunRuntimeOperationAsync(Func<Task<TunnelOperationResultDto>> operation)
    {
        if (IsBusy) return; IsBusy = true;
        try { var result = await operation(); StatusText = result.Succeeded ? LocalizedText.Get("tunnels.status.ready") : result.ProblemCode; }
        catch { StatusText = LocalizedText.Get("tunnels.status.failed"); }
        finally { IsBusy = false; }
        await RefreshAsync();
    }
    public void Dispose() => _lifetime.Cancel();
}
