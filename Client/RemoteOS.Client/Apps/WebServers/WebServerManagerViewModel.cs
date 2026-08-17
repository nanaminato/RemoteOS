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
    [ObservableProperty] private string _operationText = string.Empty;
    [ObservableProperty] private string _testResultText = string.Empty;
    [ObservableProperty] private string _selectedStatusText = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand))]
    private bool _isLoading;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(IntegrateCommand), nameof(ReloadCommand), nameof(CancelOperationCommand))]
    private bool _isOperationRunning;

    public bool IsRoot => string.Equals(_session.CurrentUser?.Username, "root", StringComparison.Ordinal);

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

    [RelayCommand(CanExecute = nameof(CanRead))]
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

    [RelayCommand(CanExecute = nameof(CanRead))]
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

    [RelayCommand(CanExecute = nameof(CanRead))]
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

    [RelayCommand(CanExecute = nameof(CanManageSelected))]
    private Task IntegrateAsync() => RunOperationAsync("integrate", SelectedServer!,
        (id, ct) => _client.IntegrateAsync(id, new IntegrateWebServerRequest(true), ct));

    [RelayCommand(CanExecute = nameof(CanManageSelected))]
    private Task ReloadAsync() => RunOperationAsync("reload", SelectedServer!,
        (id, ct) => _client.ReloadAsync(id, ct));

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private async Task CancelOperationAsync()
    {
        if (_operationCts is null) return;
        try { await _operationCts.CancelAsync(); }
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
    private bool CanRead => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer is not null;
    private bool CanManageSelected => HasManagePermission && !IsOperationRunning && SelectedServer is not null;
    private bool CanCancelOperation => IsOperationRunning;

    // nginx -t + reload is fast; a tighter poll keeps the UI responsive without spamming the host.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
}
