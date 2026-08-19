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

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(IntegrateCommand), nameof(StartManagedCommand), nameof(StopCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand))]
    private WebServerDto? _selectedServer;
    [ObservableProperty] private string _statusText = LocalizedText.Get("webservers.status.loading");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOperationActivity))]
    private string _operationText = string.Empty;
    [ObservableProperty] private string _testResultText = string.Empty;
    [ObservableProperty] private string _selectedStatusText = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(InstallManagedCommand), nameof(IntegrateCommand), nameof(StartManagedCommand), nameof(StopCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand))]
    private bool _isLoading;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(IntegrateCommand), nameof(ReloadCommand), nameof(CancelOperationCommand))]
    private bool _isOperationRunning;

    private Guid? _currentOperationId;

    public bool IsRoot => string.Equals(_session.CurrentUser?.Username, "root", StringComparison.Ordinal);
    public bool HasOperationActivity => !string.IsNullOrWhiteSpace(OperationText);

    /// <summary>Supplied by the application shell so the view model never constructs UI directly.</summary>
    public Func<Task<bool>>? RequestIntegrationConfirmationAsync { get; set; }
    public Func<Task<bool>>? RequestManagedInstallConfirmationAsync { get; set; }
    public Func<Task<bool>>? RequestManagedUninstallConfirmationAsync { get; set; }

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
            var servers = await _client.ListAsync() ?? [];
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
            var servers = await _client.DiscoverAsync() ?? [];
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

    [RelayCommand(CanExecute = nameof(CanInstallManaged))]
    private async Task InstallManagedAsync()
    {
        if (RequestManagedInstallConfirmationAsync is null || !await RequestManagedInstallConfirmationAsync()) return;
        await RunOperationAsync("install", ct => _client.InstallManagedAsync("nginx", new InstallManagedWebServerRequest(true), ct));
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartManagedAsync() => RunOperationAsync("start", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.Start, ct));

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunOperationAsync("stop", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.Stop, ct));

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => RunOperationAsync("restart", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.Restart, ct));

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => RunOperationAsync("reload", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.Reload, ct));

    [RelayCommand(CanExecute = nameof(CanUninstallManaged))]
    private async Task UninstallManagedAsync()
    {
        if (RequestManagedUninstallConfirmationAsync is null || !await RequestManagedUninstallConfirmationAsync()) return;
        await RunOperationAsync("uninstall", ct => _client.UninstallManagedAsync(SelectedServer!.Id, new UninstallManagedWebServerRequest(true), ct));
        await RefreshAsync();
    }

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
        => await RunOperationAsync(kindKey, ct => start(server.Id, ct));

    private async Task RunOperationAsync(string kindKey, Func<CancellationToken, Task<WebServerOperationDto?>> start)
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
        OperationText = LocalizedText.Format("webservers.operation.starting", OperationName(kindKey));
        try
        {
            var operation = await start(token);
            if (operation is null)
            {
                OperationText = LocalizedText.Get("webservers.operation.not_found");
                return;
            }
            if (operation.OperationId == Guid.Empty)
            {
                OperationText = LocalizedText.Format("webservers.operation.rejected", ProblemText(operation.ProblemCode));
                return;
            }
            _currentOperationId = operation.OperationId;
            operation = await PollOperationAsync(operation, token);
            if (operation.State == WebServerOperationState.Succeeded)
            {
                OperationText = LocalizedText.Format("webservers.operation.succeeded", OperationName(kindKey));
                await RefreshStatusAsync();
            }
            else if (operation.State == WebServerOperationState.Cancelled)
                OperationText = LocalizedText.Get("webservers.operation.cancelled");
            else
                OperationText = LocalizedText.Format("webservers.operation.failed", OperationName(kindKey), ProblemText(operation.ProblemCode));
        }
        catch (OperationCanceledException)
        {
            OperationText = LocalizedText.Get("webservers.operation.cancelled");
        }
        catch (WebServerApiException exception)
        {
            OperationText = LocalizedText.Format("webservers.operation.failed", OperationName(kindKey), ProblemText(exception.ProblemCode));
        }
        catch (Exception exception)
        {
            OperationText = LocalizedText.Format("webservers.operation.exception", OperationName(kindKey), exception.Message);
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
            OperationText = LocalizedText.Format("webservers.operation.progress", OperationName(operation.Kind), OperationStage(operation.Kind, operation.Stage));
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
    // A server from an older deployment can omit the capabilities object. Treat that response as
    // read-only instead of letting command re-evaluation crash while the DataGrid selects it.
    private bool CanRefreshStatus => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanRead == true;
    private bool CanTestConfiguration => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanTestConfiguration == true;
    private bool CanInstallManaged => HasManagePermission && !IsLoading && !IsOperationRunning;
    private bool CanIntegrate => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanIntegrate == true;
    private bool CanStart => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanStart == true;
    private bool CanStop => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanStop == true;
    private bool CanRestart => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanRestart == true;
    private bool CanReload => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanReload == true;
    private bool CanUninstallManaged => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanUninstall == true;
    private bool CanCancelOperation => IsOperationRunning;

    private static string OperationName(string kind) => kind switch
    {
        "install" => LocalizedText.Get("webservers.operation.kind.install", "安装 Nginx"),
        "integrate" => LocalizedText.Get("webservers.operation.kind.integrate", "集成 Nginx"),
        "uninstall" => LocalizedText.Get("webservers.operation.kind.uninstall", "卸载 Nginx"),
        "start" => LocalizedText.Get("webservers.operation.kind.start", "启动 Nginx"),
        "stop" => LocalizedText.Get("webservers.operation.kind.stop", "停止 Nginx"),
        "restart" => LocalizedText.Get("webservers.operation.kind.restart", "重启 Nginx"),
        "reload" => LocalizedText.Get("webservers.operation.kind.reload", "重载 Nginx"),
        _ => kind,
    };

    private static string OperationStage(string kind, string stage) => (kind, stage) switch
    {
        (_, "queued") => LocalizedText.Get("webservers.operation.stage.queued", "等待执行"),
        (_, "running") => LocalizedText.Get("webservers.operation.stage.running", "正在执行"),
        ("install", "installer_running") => LocalizedText.Get("webservers.operation.stage.installer_running", "正在运行安装程序"),
        ("install", "verifying_layout") => LocalizedText.Get("webservers.operation.stage.verifying_layout", "正在验证安装目录"),
        ("install", "validating_configuration") => LocalizedText.Get("webservers.operation.stage.validating_configuration", "正在验证 Nginx 配置"),
        ("install", "finalizing") => LocalizedText.Get("webservers.operation.stage.finalizing", "正在完成安装"),
        _ => stage,
    };

    private static string ProblemText(string problemCode) => problemCode switch
    {
        "webserver.install_elevation_required" => LocalizedText.Get("webservers.problem.install_elevation_required", "安装需要 RemoteOS Server 以管理员权限运行。"),
        "webserver.config_elevation_required" => LocalizedText.Get("webservers.problem.config_elevation_required", "此配置操作需要 RemoteOS Server 以管理员权限运行。"),
        "webserver.lifecycle_elevation_required" => LocalizedText.Get("webservers.problem.lifecycle_elevation_required", "此服务操作需要 RemoteOS Server 以管理员权限运行。"),
        "webserver.install_not_configured" => LocalizedText.Get("webservers.problem.install_not_configured", "服务器管理员尚未配置 Nginx 安装程序。"),
        _ => problemCode,
    };

    // nginx -t + reload is fast; a tighter poll keeps the UI responsive without spamming the host.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
}
