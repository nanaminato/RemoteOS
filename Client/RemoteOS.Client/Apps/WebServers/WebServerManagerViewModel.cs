using System.Collections.ObjectModel;
using Client.Localization;
using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Protocol.WebServers;

namespace Client.Apps.WebServers;

/// <summary>
/// Window-local web server manager state. Discovery probes the host for Nginx (and future
/// providers); integrate/reload are explicitly confirmed, marker-owned operations polled to
/// a terminal state. The server never accepts shell text or elevation credentials over HTTP.
/// </summary>
public sealed partial class WebServerManagerViewModel : ObservableObject
{
    private readonly IRemoteWebServerClient _client;
    private readonly IAuthSession _session;
    private readonly IAppPermissionScope _permissions;
    private CancellationTokenSource? _operationCts;

    public WebServerManagerViewModel(IRemoteWebServerClient client, IAuthSession session, IAppPermissionScope permissions)
    {
        _client = client;
        _session = session;
        _permissions = permissions;
    }

    public ObservableCollection<WebServerDto> Servers { get; } = [];
    public ObservableCollection<WebServerStatusDto> Statuses { get; } = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(IntegrateCommand), nameof(ReloadCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand))]
    private WebServerDto? _selectedServer;
    [ObservableProperty] private string _statusText = LocalizedText.Get("webservers.status.loading");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOperationActivity))]
    private string _operationText = string.Empty;
    [ObservableProperty] private string _testResultText = string.Empty;
    [ObservableProperty] private string _selectedStatusText = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(IntegrateCommand), nameof(ReloadCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand))]
    private bool _isLoading;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(IntegrateCommand), nameof(ReloadCommand), nameof(CancelOperationCommand))]
    private bool _isOperationRunning;

    private Guid? _currentOperationId;

    public bool IsRoot => string.Equals(_session.CurrentUser?.Username, "root", StringComparison.Ordinal);
    public bool HasOperationActivity => !string.IsNullOrWhiteSpace(OperationText);

    /// <summary>Supplied by the application shell so the view model never constructs UI directly.</summary>
    public Func<Task<bool>>? RequestIntegrationConfirmationAsync { get; set; }

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!HasReadPermission)
        {
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            StatusText = LocalizedText.Get("webservers.permission.read_required");
            return;
        }

        IsLoading = true;
        try
        {
            var servers = await _client.ListAsync();
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            SelectedStatusText = string.Empty;
            foreach (var server in servers) Servers.Add(server);
            StatusText = LocalizedText.Format("webservers.status.ready", servers.Count);
        }
        catch (Exception exception)
        {
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            StatusText = LocalizedText.Format("webservers.status.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task DiscoverAsync()
    {
        IsLoading = true;
        StatusText = LocalizedText.Get("webservers.discover.running");
        try
        {
            var servers = await _client.DiscoverAsync();
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            foreach (var server in servers) Servers.Add(server);
            StatusText = LocalizedText.Format(servers.Count > 0 ? "webservers.discover.found" : "webservers.discover.empty", servers.Count);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("webservers.discover.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshStatus))]
    private async Task RefreshStatusAsync()
    {
        if (SelectedServer is null) return;
        IsLoading = true;
        try
        {
            var status = await _client.GetStatusAsync(SelectedServer.Id);
            SelectedStatusText = status is null
                ? LocalizedText.Get("webservers.status.unavailable")
                : LocalizedText.Format("webservers.status.detail", status.RuntimeState, status.ProblemCode);
        }
        catch (Exception exception)
        {
            SelectedStatusText = LocalizedText.Format("webservers.status.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestConfiguration))]
    private async Task TestConfigurationAsync()
    {
        if (SelectedServer is null) return;
        IsLoading = true;
        TestResultText = LocalizedText.Get("webservers.test.running");
        try
        {
            var result = await _client.TestConfigurationAsync(SelectedServer.Id);
            TestResultText = result is null
                ? LocalizedText.Get("webservers.test.unavailable")
                : result.Valid
                    ? LocalizedText.Get("webservers.test.valid")
                    : LocalizedText.Format("webservers.test.invalid", result.ProblemCode);
        }
        catch (Exception exception)
        {
            TestResultText = LocalizedText.Format("webservers.test.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanIntegrate))]
    private async Task IntegrateAsync()
    {
        if (RequestIntegrationConfirmationAsync is null || !await RequestIntegrationConfirmationAsync()) return;
        await RunOperationAsync("integrate", SelectedServer!,
            (id, ct) => _client.IntegrateAsync(id, new IntegrateWebServerRequest(true), ct));
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => RunOperationAsync("reload", SelectedServer!,
        (id, ct) => _client.ReloadAsync(id, ct));

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private async Task CancelOperationAsync()
    {
        if (_operationCts is null) return;
        try
        {
            if (_currentOperationId is { } operationId)
                await _client.CancelOperationAsync(operationId);
            await _operationCts.CancelAsync();
        }
        catch (Exception) { /* cancellation is best-effort; the poll loop observes the token */ }
    }

    partial void OnSelectedServerChanged(WebServerDto? value)
    {
        SelectedStatusText = string.Empty;
        TestResultText = string.Empty;
        if (value is not null) _ = RefreshStatusAsync();
    }

    private async Task RunOperationAsync(string kindKey, WebServerDto server, Func<string, CancellationToken, Task<WebServerOperationDto?>> start)
    {
        if (!HasManagePermission)
        {
            StatusText = LocalizedText.Get("webservers.permission.manage_required");
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        IsOperationRunning = true;
        OperationText = LocalizedText.Format("webservers.operation.starting", kindKey);
        try
        {
            var operation = await start(server.Id, token);
            if (operation is null)
            {
                OperationText = LocalizedText.Get("webservers.operation.not_found");
                return;
            }
            if (operation.OperationId == Guid.Empty)
            {
                OperationText = LocalizedText.Format("webservers.operation.rejected", operation.ProblemCode);
                return;
            }
            _currentOperationId = operation.OperationId;
            operation = await PollOperationAsync(operation, token);
            if (operation.State == WebServerOperationState.Succeeded)
            {
                OperationText = LocalizedText.Format("webservers.operation.succeeded", kindKey);
                await RefreshStatusAsync();
            }
            else if (operation.State == WebServerOperationState.Cancelled)
                OperationText = LocalizedText.Get("webservers.operation.cancelled");
            else
                OperationText = LocalizedText.Format("webservers.operation.failed", kindKey, operation.ProblemCode);
        }
        catch (OperationCanceledException)
        {
            OperationText = LocalizedText.Get("webservers.operation.cancelled");
        }
        catch (Exception exception)
        {
            OperationText = LocalizedText.Format("webservers.operation.exception", kindKey, exception.Message);
        }
        finally
        {
            _currentOperationId = null;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async Task<WebServerOperationDto> PollOperationAsync(WebServerOperationDto operation, CancellationToken cancellationToken)
    {
        while (operation.State is WebServerOperationState.Queued or WebServerOperationState.Running)
        {
            OperationText = LocalizedText.Format("webservers.operation.progress", operation.Kind, operation.Stage);
            try { await Task.Delay(PollInterval, cancellationToken); }
            catch (OperationCanceledException) { return operation; }
            var updated = await _client.GetOperationAsync(operation.OperationId, cancellationToken);
            if (updated is null) break;
            operation = updated;
        }
        return operation;
    }

    private bool HasReadPermission => _permissions.IsGranted(AppPermissions.ServerWebServersRead);
    private bool HasManagePermission => HasReadPermission && _permissions.IsGranted(AppPermissions.ServerWebServersManage);
    private bool CanRefresh => HasReadPermission && !IsLoading && !IsOperationRunning;
    private bool CanDiscover => HasReadPermission && !IsLoading && !IsOperationRunning;
    private bool CanRefreshStatus => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities.CanRead == true;
    private bool CanTestConfiguration => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities.CanTestConfiguration == true;
    private bool CanIntegrate => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities.CanIntegrate == true;
    private bool CanReload => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities.CanReload == true;
    private bool CanCancelOperation => IsOperationRunning;

    // nginx -t + reload is fast; a tighter poll keeps the UI responsive without spamming the host.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
}
