using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Tunnels;

namespace Client.Apps.Tunnels;

/// <summary>Client-side FRP control-plane state. It never retains generated TOML or secrets after a request completes.</summary>
public sealed partial class TunnelManagerViewModel(IRemoteTunnelClient client, bool canManage) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    public ObservableCollection<TunnelServerProfileDto> Profiles { get; } = [];
    public ObservableCollection<TunnelDefinitionDto> Tunnels { get; } = [];
    public IReadOnlyList<TunnelProtocol> Protocols { get; } = Enum.GetValues<TunnelProtocol>();
    public IReadOnlyList<TunnelAuthKind> AuthKinds { get; } = Enum.GetValues<TunnelAuthKind>();
    public IReadOnlyList<TunnelTlsMode> TlsModes { get; } = Enum.GetValues<TunnelTlsMode>();
    public IReadOnlyList<TunnelRuntimeMode> RuntimeModes { get; } = Enum.GetValues<TunnelRuntimeMode>();

    [ObservableProperty] private TunnelServerProfileDto? _selectedProfile;
    [ObservableProperty] private TunnelDefinitionDto? _selectedTunnel;
    [ObservableProperty] private TunnelRuntimeDto? _runtime;
    [ObservableProperty] private string _runtimeText = "—";
    [ObservableProperty] private string _statusText = LocalizedText.Get("tunnels.status.loading");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _logsText = string.Empty;
    [ObservableProperty] private string _runtimeVersion = "v0.71.0";
    [ObservableProperty] private bool _runtimeInstallConfirmed;

    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _profileHost = string.Empty;
    [ObservableProperty] private int _profilePort = 7000;
    [ObservableProperty] private TunnelAuthKind _profileAuthKind = TunnelAuthKind.Token;
    [ObservableProperty] private TunnelTlsMode _profileTlsMode = TunnelTlsMode.Default;
    [ObservableProperty] private TunnelRuntimeMode _profileRuntimeMode = TunnelRuntimeMode.Managed;
    [ObservableProperty] private string _profileExternalPath = string.Empty;
    [ObservableProperty] private string _profileToken = string.Empty;
    [ObservableProperty] private bool _confirmProfileDeletion;

    [ObservableProperty] private string _tunnelName = string.Empty;
    [ObservableProperty] private TunnelServerProfileDto? _tunnelProfile;
    [ObservableProperty] private TunnelProtocol _tunnelProtocol = TunnelProtocol.Tcp;
    [ObservableProperty] private string _tunnelLocalHost = "127.0.0.1";
    [ObservableProperty] private int _tunnelLocalPort = 8080;
    [ObservableProperty] private int? _tunnelRemotePort = 8080;
    [ObservableProperty] private string _tunnelDomain = string.Empty;
    [ObservableProperty] private bool _tunnelEnabled = true;
    [ObservableProperty] private bool _tunnelEncryption;
    [ObservableProperty] private bool _tunnelCompression;
    [ObservableProperty] private bool _confirmTunnelDeletion;

    public bool CanManage => canManage;
    public bool HasSelectedProfile => SelectedProfile is not null;
    public bool HasSelectedTunnel => SelectedTunnel is not null;
    public bool IsManagedProfile => ProfileRuntimeMode == RemoteOS.Protocol.Tunnels.TunnelRuntimeMode.Managed;
    public bool IsTokenAuth => ProfileAuthKind == RemoteOS.Protocol.Tunnels.TunnelAuthKind.Token;
    public int ConnectedTunnelCount => Tunnels.Count(x => x.State == TunnelConnectionState.Connected);
    public int SavedTunnelCount => Tunnels.Count(x => x.State == TunnelConnectionState.SavedNotApplied);
    public bool CanInstallRuntime => CanManage && RuntimeInstallConfirmed && !IsBusy;
    public bool CanDeleteProfile => CanManage && HasSelectedProfile && ConfirmProfileDeletion && !IsBusy;
    public bool CanDeleteTunnel => CanManage && HasSelectedTunnel && ConfirmTunnelDeletion && !IsBusy;

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var profilesTask = client.ListProfilesAsync(_lifetime.Token);
            var tunnelsTask = client.ListAsync(_lifetime.Token);
            var runtimeTask = client.GetRuntimeAsync(_lifetime.Token);
            await Task.WhenAll(profilesTask, tunnelsTask, runtimeTask);
            Replace(Profiles, await profilesTask); Replace(Tunnels, await tunnelsTask);
            var runtime = await runtimeTask;
            Runtime = runtime; RuntimeText = FormatRuntime(runtime);
            KeepSelections(); UpdateCounters(); StatusText = LocalizedText.Get("tunnels.status.ready");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void NewProfile()
    {
        SelectedProfile = null;
        ProfileName = ProfileHost = ProfileExternalPath = ProfileToken = string.Empty;
        ProfilePort = 7000; ProfileAuthKind = TunnelAuthKind.Token; ProfileTlsMode = TunnelTlsMode.Default; ProfileRuntimeMode = TunnelRuntimeMode.Managed; ConfirmProfileDeletion = false;
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SaveProfileAsync()
    {
        if (IsBusy) return;
        await RunWriteAsync(async () =>
        {
            var request = new UpsertTunnelServerProfileRequest(ProfileName, ProfileHost, ProfilePort, ProfileAuthKind, ProfileTlsMode, ProfileRuntimeMode,
                ProfileRuntimeMode == TunnelRuntimeMode.External ? ProfileExternalPath : null, SelectedProfile?.Revision);
            var saved = SelectedProfile is null ? await client.CreateProfileAsync(request, _lifetime.Token) : await client.UpdateProfileAsync(SelectedProfile.Id, request, _lifetime.Token);
            if (ProfileAuthKind == TunnelAuthKind.Token && !string.IsNullOrWhiteSpace(ProfileToken)) await client.SetProfileTokenAsync(saved.Id, ProfileToken, _lifetime.Token);
            ProfileToken = string.Empty; SelectedProfile = saved; TunnelProfile ??= saved; StatusText = LocalizedText.Get("tunnels.status.profile_saved");
        });
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfileAsync()
    {
        var selected = SelectedProfile; if (selected is null) return;
        await RunWriteAsync(async () => { await client.DeleteProfileAsync(selected.Id, _lifetime.Token); NewProfile(); StatusText = LocalizedText.Get("tunnels.status.profile_deleted"); });
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ProbeExternalRuntimeAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(ProfileExternalPath)) return;
        IsBusy = true;
        try { var probe = await client.DetectExternalRuntimeAsync(ProfileExternalPath, _lifetime.Token); StatusText = probe.State == TunnelRuntimeState.Available ? LocalizedText.Format("tunnels.status.external_available", probe.Version ?? "—") : probe.ProblemCode; }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void NewTunnel()
    {
        SelectedTunnel = null; TunnelName = TunnelDomain = string.Empty; TunnelProfile = SelectedProfile ?? Profiles.FirstOrDefault();
        TunnelProtocol = RemoteOS.Protocol.Tunnels.TunnelProtocol.Tcp; TunnelLocalHost = "127.0.0.1"; TunnelLocalPort = 8080; TunnelRemotePort = 8080; TunnelEnabled = true; TunnelEncryption = TunnelCompression = false; ConfirmTunnelDeletion = false;
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SaveTunnelAsync()
    {
        if (IsBusy || TunnelProfile is null) { StatusText = "tunnel.profile_not_found"; return; }
        await RunWriteAsync(async () =>
        {
            var request = new UpsertTunnelDefinitionRequest(TunnelProfile.Id, TunnelName, TunnelProtocol, TunnelLocalHost, TunnelLocalPort, TunnelRemotePort,
                string.IsNullOrWhiteSpace(TunnelDomain) ? null : TunnelDomain, TunnelEnabled, TunnelEncryption, TunnelCompression, SelectedTunnel?.Revision);
            var saved = SelectedTunnel is null ? await client.CreateTunnelAsync(request, _lifetime.Token) : await client.UpdateTunnelAsync(SelectedTunnel.Id, request, _lifetime.Token);
            SelectedTunnel = saved; StatusText = LocalizedText.Get("tunnels.status.tunnel_saved");
        });
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTunnel))]
    private async Task DeleteTunnelAsync()
    {
        var selected = SelectedTunnel; if (selected is null) return;
        await RunWriteAsync(async () => { await client.DeleteTunnelAsync(selected.Id, _lifetime.Token); NewTunnel(); StatusText = LocalizedText.Get("tunnels.status.tunnel_deleted"); });
    }

    [RelayCommand(CanExecute = nameof(CanApplySelected))] private Task ApplySelectedAsync() => RunProfileOperationAsync(profile => client.ApplyAsync(profile.Id, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanApplySelected))] private Task StopSelectedAsync() => RunProfileOperationAsync(profile => client.StopAsync(profile.Id, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private async Task LoadLogsAsync()
    {
        if (SelectedProfile is null) return;
        try { LogsText = string.Join(Environment.NewLine, (await client.GetLogsAsync(SelectedProfile.Id, _lifetime.Token)).Select(log => $"{log.Timestamp:HH:mm:ss} {log.Level}: {log.Message}")); }
        catch (Exception ex) { StatusText = ProblemText(ex); }
    }
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))] private Task InstallRuntimeAsync() => RunRuntimeOperationAsync(() => client.InstallManagedRuntimeAsync(RuntimeVersion, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanManage))] private Task RollbackRuntimeAsync() => RunRuntimeOperationAsync(() => client.RollbackManagedRuntimeAsync(_lifetime.Token));

    private bool CanApplySelected => CanManage && HasSelectedProfile && !IsBusy;
    partial void OnSelectedProfileChanged(TunnelServerProfileDto? value)
    {
        if (value is not null) { ProfileName = value.Name; ProfileHost = value.Host; ProfilePort = value.Port; ProfileAuthKind = value.AuthKind; ProfileTlsMode = value.TlsMode; ProfileRuntimeMode = value.RuntimeMode; ProfileExternalPath = value.ExternalExecutablePath ?? string.Empty; ProfileToken = string.Empty; TunnelProfile ??= value; }
        ConfirmProfileDeletion = false; NotifyProfileCommands();
    }
    partial void OnSelectedTunnelChanged(TunnelDefinitionDto? value)
    {
        if (value is not null) { TunnelName = value.Name; TunnelProfile = Profiles.FirstOrDefault(x => x.Id == value.ServerProfileId); TunnelProtocol = value.Protocol; TunnelLocalHost = value.LocalHost; TunnelLocalPort = value.LocalPort; TunnelRemotePort = value.RemotePort; TunnelDomain = value.Domain ?? string.Empty; TunnelEnabled = value.Enabled; TunnelEncryption = value.Encryption; TunnelCompression = value.Compression; }
        ConfirmTunnelDeletion = false; NotifyTunnelCommands();
    }
    partial void OnProfileRuntimeModeChanged(TunnelRuntimeMode value) => OnPropertyChanged(nameof(IsManagedProfile));
    partial void OnProfileAuthKindChanged(TunnelAuthKind value) => OnPropertyChanged(nameof(IsTokenAuth));
    partial void OnRuntimeInstallConfirmedChanged(bool value) => InstallRuntimeCommand.NotifyCanExecuteChanged();
    partial void OnConfirmProfileDeletionChanged(bool value) => DeleteProfileCommand.NotifyCanExecuteChanged();
    partial void OnConfirmTunnelDeletionChanged(bool value) => DeleteTunnelCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) { NotifyProfileCommands(); NotifyTunnelCommands(); InstallRuntimeCommand.NotifyCanExecuteChanged(); RollbackRuntimeCommand.NotifyCanExecuteChanged(); }
    private void NotifyProfileCommands() { ApplySelectedCommand.NotifyCanExecuteChanged(); StopSelectedCommand.NotifyCanExecuteChanged(); LoadLogsCommand.NotifyCanExecuteChanged(); DeleteProfileCommand.NotifyCanExecuteChanged(); }
    private void NotifyTunnelCommands() => DeleteTunnelCommand.NotifyCanExecuteChanged();

    private async Task RunProfileOperationAsync(Func<TunnelServerProfileDto, Task<TunnelOperationResultDto>> operation)
    {
        if (IsBusy || SelectedProfile is null) return; IsBusy = true;
        try { var result = await operation(SelectedProfile); StatusText = result.Succeeded ? LocalizedText.Format("tunnels.status.operation", result.State) : result.ProblemCode; }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
        await RefreshAsync();
    }
    private async Task RunRuntimeOperationAsync(Func<Task<TunnelOperationResultDto>> operation)
    {
        if (IsBusy) return; IsBusy = true;
        try { var result = await operation(); StatusText = result.Succeeded ? LocalizedText.Get("tunnels.status.runtime_updated") : result.ProblemCode; }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
        await RefreshAsync();
    }
    private async Task RunWriteAsync(Func<Task> operation)
    {
        if (IsBusy) return; IsBusy = true;
        try { await operation(); }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
        await RefreshAsync();
    }
    private void KeepSelections()
    {
        if (SelectedProfile is { } profile) SelectedProfile = Profiles.FirstOrDefault(x => x.Id == profile.Id);
        if (SelectedTunnel is { } tunnel) SelectedTunnel = Tunnels.FirstOrDefault(x => x.Id == tunnel.Id);
        if (TunnelProfile is { } selected) TunnelProfile = Profiles.FirstOrDefault(x => x.Id == selected.Id) ?? Profiles.FirstOrDefault();
    }
    private void UpdateCounters() { OnPropertyChanged(nameof(ConnectedTunnelCount)); OnPropertyChanged(nameof(SavedTunnelCount)); }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { collection.Clear(); foreach (var value in values) collection.Add(value); }
    private static string FormatRuntime(TunnelRuntimeDto runtime) => string.Join(" · ", new[] { runtime.State.ToString(), runtime.Version, string.IsNullOrEmpty(runtime.ProblemCode) ? null : runtime.ProblemCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
    private static string ProblemText(Exception ex) => ex is TunnelRequestException request ? request.ProblemCode : LocalizedText.Get("tunnels.status.failed");
    public void Dispose() => _lifetime.Cancel();
}
