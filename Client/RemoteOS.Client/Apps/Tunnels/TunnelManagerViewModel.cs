using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Tunnels;

namespace Client.Apps.Tunnels;

/// <summary>Shared state for the tunnel list, runtime page, and independent child windows.</summary>
public sealed partial class TunnelManagerViewModel(IRemoteTunnelClient client, bool canManage) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    public ObservableCollection<TunnelServerProfileDto> Profiles { get; } = [];
    public ObservableCollection<TunnelDefinitionDto> Tunnels { get; } = [];

    [ObservableProperty] private TunnelServerProfileDto? _selectedProfile;
    [ObservableProperty] private TunnelDefinitionDto? _selectedTunnel;
    [ObservableProperty] private TunnelRuntimeDto? _runtime;
    [ObservableProperty] private TunnelRuntimeInstallationDto _runtimeInstallation = new(TunnelRuntimeInstallationState.Idle, null, 0);
    [ObservableProperty] private string _runtimeText = "—";
    [ObservableProperty] private string _statusText = LocalizedText.Get("tunnels.status.loading");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _runtimeVersion = "v0.71.0";
    [ObservableProperty] private bool _runtimeInstallConfirmed;

    public Func<TunnelServerProfileDto?, Task>? OpenProfileEditorAsync { get; set; }
    public Func<TunnelDefinitionDto?, Task>? OpenTunnelEditorAsync { get; set; }
    public Func<TunnelServerProfileDto, Task>? OpenLogsWindowAsync { get; set; }

    public bool CanManage => canManage;
    public bool HasSelectedProfile => SelectedProfile is not null;
    public bool HasSelectedTunnel => SelectedTunnel is not null;
    public int ConnectedTunnelCount => Tunnels.Count(x => x.State == TunnelConnectionState.Connected);
    public int SavedTunnelCount => Tunnels.Count(x => x.State == TunnelConnectionState.SavedNotApplied);
    public bool CanInstallRuntime => CanManage && RuntimeInstallConfirmed && !IsBusy;
    public bool RuntimeInstallationInProgress => RuntimeInstallation.State is not TunnelRuntimeInstallationState.Idle and not TunnelRuntimeInstallationState.Succeeded and not TunnelRuntimeInstallationState.Failed;
    public int RuntimeInstallationProgress => RuntimeInstallation.Progress;
    public string RuntimeInstallationText => FormatInstallation(RuntimeInstallation);

    public async Task StartAsync() => await RefreshAsync();
    public Task RefreshAfterChildAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await RefreshCoreAsync(); StatusText = LocalizedText.Get("tunnels.status.ready"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task NewProfileAsync() => OpenProfileEditorAsync?.Invoke(null) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task EditProfileAsync() => SelectedProfile is { } profile
        ? OpenProfileEditorAsync?.Invoke(profile) ?? Task.CompletedTask
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task NewTunnelAsync() => OpenTunnelEditorAsync?.Invoke(null) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task EditTunnelAsync() => SelectedTunnel is { } tunnel
        ? OpenTunnelEditorAsync?.Invoke(tunnel) ?? Task.CompletedTask
        : Task.CompletedTask;

    [RelayCommand]
    private Task OpenLogsAsync(TunnelServerProfileDto? profile) => profile is null
        ? Task.CompletedTask
        : OpenLogsWindowAsync?.Invoke(profile) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanApplySelected))] private Task ApplySelectedAsync() => RunProfileOperationAsync(profile => client.ApplyAsync(profile.Id, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanApplySelected))] private Task StopSelectedAsync() => RunProfileOperationAsync(profile => client.StopAsync(profile.Id, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))] private Task InstallRuntimeAsync() => RunRuntimeOperationAsync(() => client.InstallManagedRuntimeAsync(RuntimeVersion, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanManage))] private Task RollbackRuntimeAsync() => RunRuntimeOperationAsync(() => client.RollbackManagedRuntimeAsync(_lifetime.Token));

    private bool CanApplySelected => CanManage && HasSelectedProfile && !IsBusy;
    partial void OnSelectedProfileChanged(TunnelServerProfileDto? value) => NotifyProfileCommands();
    partial void OnSelectedTunnelChanged(TunnelDefinitionDto? value) => EditTunnelCommand.NotifyCanExecuteChanged();
    partial void OnRuntimeInstallConfirmedChanged(bool value) => InstallRuntimeCommand.NotifyCanExecuteChanged();
    partial void OnRuntimeInstallationChanged(TunnelRuntimeInstallationDto value)
    {
        OnPropertyChanged(nameof(RuntimeInstallationInProgress));
        OnPropertyChanged(nameof(RuntimeInstallationProgress));
        OnPropertyChanged(nameof(RuntimeInstallationText));
    }
    partial void OnIsBusyChanged(bool value)
    {
        NotifyProfileCommands(); EditTunnelCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged(); RollbackRuntimeCommand.NotifyCanExecuteChanged();
    }
    private void NotifyProfileCommands()
    {
        ApplySelectedCommand.NotifyCanExecuteChanged(); StopSelectedCommand.NotifyCanExecuteChanged();
        EditProfileCommand.NotifyCanExecuteChanged();
    }

    private async Task RunProfileOperationAsync(Func<TunnelServerProfileDto, Task<TunnelOperationResultDto>> operation)
    {
        if (IsBusy || SelectedProfile is null) return;
        IsBusy = true;
        try
        {
            var result = await operation(SelectedProfile);
            StatusText = result.Succeeded ? LocalizedText.Format("tunnels.status.operation", result.State) : result.ProblemCode;
        }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    private async Task RunRuntimeOperationAsync(Func<Task<TunnelOperationResultDto>> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var operationTask = operation();
            while (!operationTask.IsCompleted)
            {
                try { RuntimeInstallation = await client.GetRuntimeInstallationStatusAsync(_lifetime.Token); }
                catch (OperationCanceledException) { throw; }
                catch { /* The install request remains authoritative; retry on the next poll. */ }
                await Task.WhenAny(operationTask, Task.Delay(400, _lifetime.Token));
            }
            var result = await operationTask;
            try { RuntimeInstallation = await client.GetRuntimeInstallationStatusAsync(_lifetime.Token); } catch { }
            StatusText = result.Succeeded ? LocalizedText.Get("tunnels.status.runtime_updated") : result.ProblemCode;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    private async Task RefreshCoreAsync()
    {
        var profilesTask = client.ListProfilesAsync(_lifetime.Token);
        var tunnelsTask = client.ListAsync(_lifetime.Token);
        var runtimeTask = client.GetRuntimeAsync(_lifetime.Token);
        var installationTask = client.GetRuntimeInstallationStatusAsync(_lifetime.Token);
        await Task.WhenAll(profilesTask, tunnelsTask, runtimeTask, installationTask);
        Replace(Profiles, await profilesTask); Replace(Tunnels, await tunnelsTask);
        Runtime = await runtimeTask; RuntimeText = FormatRuntime(Runtime);
        RuntimeInstallation = await installationTask;
        KeepSelections(); UpdateCounters();
    }

    private void KeepSelections()
    {
        if (SelectedProfile is { } profile) SelectedProfile = Profiles.FirstOrDefault(x => x.Id == profile.Id);
        if (SelectedTunnel is { } tunnel) SelectedTunnel = Tunnels.FirstOrDefault(x => x.Id == tunnel.Id);
    }
    private void UpdateCounters() { OnPropertyChanged(nameof(ConnectedTunnelCount)); OnPropertyChanged(nameof(SavedTunnelCount)); }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { collection.Clear(); foreach (var value in values) collection.Add(value); }
    private static string FormatRuntime(TunnelRuntimeDto runtime) => string.Join(" · ", new[] { runtime.State.ToString(), runtime.Version, string.IsNullOrEmpty(runtime.ProblemCode) ? null : runtime.ProblemCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
    private static string FormatInstallation(TunnelRuntimeInstallationDto installation)
    {
        var state = LocalizedText.Get($"tunnels.runtime.install_state.{installation.State}");
        return installation.State is TunnelRuntimeInstallationState.Idle or TunnelRuntimeInstallationState.Succeeded or TunnelRuntimeInstallationState.Failed
            ? string.IsNullOrEmpty(installation.ProblemCode) ? state : $"{state}: {installation.ProblemCode}"
            : LocalizedText.Format("tunnels.runtime.install_progress", state, installation.Progress);
    }
    private static string ProblemText(Exception ex) => ex is TunnelRequestException request ? request.ProblemCode : LocalizedText.Get("tunnels.status.failed");
    public void Dispose() => _lifetime.Cancel();
}
