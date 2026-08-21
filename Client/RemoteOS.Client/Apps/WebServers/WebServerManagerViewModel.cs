using System.Collections.ObjectModel;
using Client.Apps.Certificates;
using Client.Localization;
using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Protocol.Certificates;
using RemoteOS.Protocol.Common;
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
    private readonly IRemoteCertificateClient _certificates;
    private readonly IAuthSession _session;
    private readonly IAppPermissionScope _permissions;
    private CancellationTokenSource? _operationCts;

    public WebServerManagerViewModel(IRemoteWebServerClient client, IRemoteCertificateClient certificates, IAuthSession session, IAppPermissionScope permissions)
    {
        _client = client;
        _certificates = certificates;
        _session = session;
        _permissions = permissions;
        InstallVersion = string.Empty;
    }

    public ObservableCollection<WebServerDto> Servers { get; } = [];
    public ObservableCollection<WebServerStatusDto> Statuses { get; } = [];
    public ObservableCollection<string> AvailableWindowsVersions { get; } = [];
    public ObservableCollection<WebServerSiteDto> Sites { get; } = [];
    public ObservableCollection<CertificateDto> Certificates { get; } = [];
    public ObservableCollection<WebServerSiteBindingEditor> SiteBindings { get; } = [];
    public IReadOnlyList<WebServerSiteKind> SiteKinds { get; } = [WebServerSiteKind.ReverseProxy, WebServerSiteKind.Static];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IntegrateCommand), nameof(StartManagedCommand), nameof(StopCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand))]
    [NotifyPropertyChangedFor(nameof(IsExternalServer), nameof(IsIntegratedServer), nameof(IsManagedServer), nameof(IsIntegratedOrManagedServer), nameof(ManagementHint))]
    private WebServerDto? _selectedServer;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(EditSiteCommand))]
    private WebServerSiteDto? _selectedSite;
    [ObservableProperty] private string _statusText = LocalizedText.Get("webservers.status.loading");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOperationActivity))]
    private string _operationText = string.Empty;
    [ObservableProperty] private string _testResultText = string.Empty;
    [ObservableProperty] private string _selectedStatusText = string.Empty;
    [ObservableProperty] private string _installVersion = string.Empty;
    [ObservableProperty] private string _localPackageName = string.Empty;
    [ObservableProperty] private string _siteName = string.Empty;
    [ObservableProperty] private string _siteBindingsBatch = string.Empty;
    [ObservableProperty] private string _siteUpstream = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReverseProxySite), nameof(IsStaticSite))] private WebServerSiteKind _selectedSiteKind = WebServerSiteKind.ReverseProxy;
    [ObservableProperty] private bool _siteHttpsEnabled;
    [ObservableProperty] private CertificateDto? _selectedSiteCertificate;
    [ObservableProperty] private string _siteStatusText = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(RefreshWindowsVersionsCommand), nameof(InstallManagedCommand), nameof(SelectLocalPackageCommand), nameof(IntegrateCommand), nameof(StartManagedCommand), nameof(StopCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand))]
    private bool _isLoading;
    // Every action uses IsOperationRunning in its CanExecute predicate. Keep the command state
    // in sync before and after polling, otherwise controls can retain a stale disabled state.
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(RefreshWindowsVersionsCommand), nameof(InstallManagedCommand), nameof(SelectLocalPackageCommand), nameof(IntegrateCommand), nameof(StartManagedCommand), nameof(StopCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand), nameof(CancelOperationCommand))]
    private bool _isOperationRunning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsManagedInstallAvailable))]
    private bool _hasManagedInstallation;

    private Guid? _currentOperationId;
    private string? _localPackageId;

    public bool IsRoot => string.Equals(_session.CurrentUser?.Username, "root", StringComparison.Ordinal);
    public bool IsWindowsServer => _session.CurrentServer?.Platform == PlatformKind.Windows;
    public bool IsLinuxServer => _session.CurrentServer?.Platform == PlatformKind.Linux;
    public bool HasOperationActivity => !string.IsNullOrWhiteSpace(OperationText);
    public bool IsReverseProxySite => SelectedSiteKind == WebServerSiteKind.ReverseProxy;
    public bool IsStaticSite => SelectedSiteKind == WebServerSiteKind.Static;
    public bool IsExternalServer => SelectedServer?.ManagementMode == WebServerManagementMode.External;
    public bool IsIntegratedServer => SelectedServer?.ManagementMode == WebServerManagementMode.Integrated;
    public bool IsManagedServer => SelectedServer?.ManagementMode == WebServerManagementMode.Managed;
    public bool IsIntegratedOrManagedServer => IsIntegratedServer || IsManagedServer;
    public bool IsManagedInstallAvailable => !HasManagedInstallation;
    public string ManagementHint => SelectedServer?.ManagementMode switch
    {
        WebServerManagementMode.Integrated => "此 Nginx 已集成：RemoteOS 可以管理站点配置并重载配置，但不会启动、停止或重启外部安装的 Nginx。如需完整生命周期管理，请使用受管安装。",
        WebServerManagementMode.Managed => "此 Nginx 由 RemoteOS 安装和管理，可使用完整的服务生命周期操作。",
        WebServerManagementMode.External => "此 Nginx 尚未集成。集成后可由 RemoteOS 管理站点配置和重载配置。",
        _ => "请选择一个 Nginx 实例以查看可用操作。",
    };

    /// <summary>Supplied by the application shell so the view model never constructs UI directly.</summary>
    public Func<Task<bool>>? RequestIntegrationConfirmationAsync { get; set; }
    public Func<Task<bool>>? RequestManagedInstallConfirmationAsync { get; set; }
    public Func<Task<ManagedInstallExistingDirectoryAction?>>? RequestExistingManagedInstallActionAsync { get; set; }
    public Func<Task<bool>>? RequestManagedUninstallConfirmationAsync { get; set; }
    public Func<Task<string?>>? RequestLocalNginxPackageAsync { get; set; }
    /// <summary>Routes a known static-site directory into RemoteExplorer.</summary>
    public Func<string, Task>? OpenFileBrowserAtPathAsync { get; set; }
    /// <summary>Provided by the application shell to keep the editor in a modal dialog.</summary>
    public Func<bool, Task>? ShowSiteEditorAsync { get; set; }
    /// <summary>Set only while the site editor dialog is open.</summary>
    public Func<Task>? CloseSiteEditorAsync { get; set; }
    /// <summary>Set while the editor is open so validation errors can appear above the modal form.</summary>
    public Func<string, Task>? ShowSiteSaveErrorAsync { get; set; }

    public async Task StartAsync()
    {
        await RefreshAsync();
        await LoadCertificatesAsync();
        if (IsWindowsServer) await LoadWindowsVersionsAsync();
    }

    private async Task LoadWindowsVersionsAsync()
    {
        IsLoading = true;
        try
        {
            var catalog = await _client.GetManagedInstallCatalogAsync("nginx");
            AvailableWindowsVersions.Clear();
            foreach (var version in catalog?.Versions ?? []) AvailableWindowsVersions.Add(version);
            if (catalog is null || catalog.Versions.Count == 0 || !string.IsNullOrWhiteSpace(catalog.ProblemCode))
            {
                InstallVersion = string.Empty;
                StatusText = LocalizedText.Get("webservers.version_catalog.unavailable");
                return;
            }
            if (string.IsNullOrWhiteSpace(InstallVersion))
                InstallVersion = catalog.MainlineVersion ?? catalog.StableVersion ?? string.Empty;
            StatusText = LocalizedText.Format("webservers.version_catalog.ready", catalog.Versions.Count);
        }
        catch (Exception)
        {
            AvailableWindowsVersions.Clear();
            InstallVersion = string.Empty;
            StatusText = LocalizedText.Get("webservers.version_catalog.unavailable");
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshWindowsVersions))]
    private Task RefreshWindowsVersionsAsync() => LoadWindowsVersionsAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!HasReadPermission)
        {
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            HasManagedInstallation = false;
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
            HasManagedInstallation = servers.Any(server => server.ManagementMode == WebServerManagementMode.Managed);
            StatusText = LocalizedText.Format("webservers.status.ready", servers.Count);
        }
        catch (Exception exception)
        {
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            HasManagedInstallation = false;
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
            HasManagedInstallation = servers.Any(server => server.ManagementMode == WebServerManagementMode.Managed);
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
        if (IsWindowsServer && string.IsNullOrWhiteSpace(_localPackageId) && string.IsNullOrWhiteSpace(InstallVersion))
        {
            StatusText = LocalizedText.Get("webservers.problem.version_required");
            return;
        }
        if (RequestManagedInstallConfirmationAsync is null || !await RequestManagedInstallConfirmationAsync()) return;
        var version = string.IsNullOrWhiteSpace(_localPackageId) ? InstallVersion.Trim() : null;
        try
        {
            await RunOperationAsync("install", ct => _client.InstallManagedAsync("nginx", new InstallManagedWebServerRequest(true,
                version, _localPackageId), ct), rethrowApiException: true);
        }
        catch (WebServerApiException exception) when (exception.ProblemCode == "webserver.managed_installation_exists")
        {
            var action = await (RequestExistingManagedInstallActionAsync?.Invoke() ?? Task.FromResult<ManagedInstallExistingDirectoryAction?>(null));
            if (action is not (ManagedInstallExistingDirectoryAction.Reuse or ManagedInstallExistingDirectoryAction.Replace))
            {
                OperationText = LocalizedText.Get("webservers.managed.existing.cancelled");
                return;
            }
            await RunOperationAsync("install", ct => _client.InstallManagedAsync("nginx", new InstallManagedWebServerRequest(true,
                version, _localPackageId, action.Value), ct));
        }
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanInstallManaged))]
    private async Task SelectLocalPackageAsync()
    {
        if (RequestLocalNginxPackageAsync is null) return;
        var path = await RequestLocalNginxPackageAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await using var package = File.OpenRead(path);
            var uploaded = await _client.UploadManagedPackageAsync("nginx", Path.GetFileName(path), package);
            if (uploaded is null)
            {
                StatusText = LocalizedText.Get("webservers.package.invalid");
                return;
            }
            _localPackageId = uploaded.Id;
            LocalPackageName = uploaded.FileName;
            StatusText = LocalizedText.Format("webservers.package.ready", uploaded.FileName);
        }
        catch (WebServerApiException exception)
        {
            StatusText = LocalizedText.Format("webservers.package.failed", ProblemText(exception.ProblemCode));
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("webservers.package.failed", exception.Message);
        }
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

    [RelayCommand(CanExecute = nameof(CanOpenSiteEditor))]
    private async Task NewSiteAsync()
    {
        ResetSiteEditor();
        SiteStatusText = "填写站点信息后保存。";
        if (ShowSiteEditorAsync is not null) await ShowSiteEditorAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanEditSite))]
    private async Task EditSiteAsync()
    {
        if (SelectedSite is null) return;
        if (ShowSiteEditorAsync is not null) await ShowSiteEditorAsync(true);
    }

    [RelayCommand(CanExecute = nameof(CanSaveSite))]
    private async Task SaveSiteAsync()
    {
        if (SelectedServer is null || !HasManagePermission) return;
        var bindings = SiteBindings
            .Select(binding => new WebServerSiteBindingDto(binding.Domain.Trim(), binding.Port))
            .Where(binding => !string.IsNullOrWhiteSpace(binding.Domain))
            .ToArray();
        if (bindings.Length == 0 || string.IsNullOrWhiteSpace(SiteName) || (IsReverseProxySite && string.IsNullOrWhiteSpace(SiteUpstream)) || (SiteHttpsEnabled && SelectedSiteCertificate is null))
        {
            await ReportSiteSaveErrorAsync("请至少填写一组域名或 IP 与端口，并完成必要的站点配置；启用 HTTPS 时请选择证书。");
            return;
        }
        try
        {
            var domains = bindings.Select(binding => binding.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var saved = await _client.UpsertSiteAsync(SelectedServer.Id, new UpsertWebServerSiteRequest(SelectedSite?.Id, SiteName.Trim(), SelectedSiteKind, domains, bindings[0].Port,
                IsReverseProxySite ? SiteUpstream.Trim() : null, null, SelectedSiteCertificate?.Id, SiteHttpsEnabled, bindings));
            if (saved is null) { await ReportSiteSaveErrorAsync("保存失败。请确认 Nginx 已集成、配置有效且服务端以管理员权限运行。"); return; }
            await LoadSitesAsync();
            SelectedSite = Sites.FirstOrDefault(site => site.Id == saved.Id);
            SiteStatusText = "站点已保存，Nginx 配置已验证并生效。";
            if (CloseSiteEditorAsync is not null) await CloseSiteEditorAsync();
        }
        catch (WebServerApiException exception) { await ReportSiteSaveErrorAsync(SiteSaveProblemText(exception.ProblemCode)); }
        catch (Exception exception) { await ReportSiteSaveErrorAsync($"保存站点失败：{exception.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSite))]
    private async Task DeleteSiteAsync()
    {
        if (SelectedServer is null || SelectedSite is null || !HasManagePermission) return;
        try
        {
            await _client.DeleteSiteAsync(SelectedServer.Id, SelectedSite.Id);
            ResetSiteEditor();
            await LoadSitesAsync();
            SiteStatusText = "站点已删除，Nginx 已重载。";
        }
        catch (Exception exception) { SiteStatusText = $"删除站点失败：{exception.Message}"; }
    }

    private void ResetSiteEditor()
    {
        SelectedSite = null;
        SiteName = string.Empty;
        SiteBindingsBatch = string.Empty;
        SiteBindings.Clear();
        SiteBindings.Add(new WebServerSiteBindingEditor());
        SiteUpstream = string.Empty;
        SelectedSiteKind = WebServerSiteKind.ReverseProxy;
        SiteHttpsEnabled = false;
        SelectedSiteCertificate = null;
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
        SelectedStatusText = ManagementHint;
        TestResultText = string.Empty;
        SelectedSite = null;
        Sites.Clear();
        if (value is not null)
        {
            _ = RefreshStatusAsync();
            _ = LoadSitesAsync();
        }
    }

    partial void OnSelectedSiteChanged(WebServerSiteDto? value)
    {
        if (value is null) return;
        SiteName = value.Name;
        SiteBindingsBatch = string.Empty;
        SiteBindings.Clear();
        foreach (var binding in value.EffectiveBindings)
            SiteBindings.Add(new WebServerSiteBindingEditor(binding.Domain, binding.Port));
        SiteUpstream = value.Upstream ?? string.Empty;
        SelectedSiteKind = value.Kind;
        SiteHttpsEnabled = value.HttpsEnabled;
        SelectedSiteCertificate = Certificates.FirstOrDefault(certificate => certificate.Id == value.CertificateId);
    }

    private async Task LoadCertificatesAsync()
    {
        try
        {
            var certificates = await _certificates.ListAsync();
            Certificates.Clear();
            foreach (var certificate in certificates.Where(certificate => certificate.Status is CertificateStatus.Active or CertificateStatus.Issued)) Certificates.Add(certificate);
        }
        catch { Certificates.Clear(); }
    }

    private async Task LoadSitesAsync()
    {
        Sites.Clear();
        if (SelectedServer is null) return;
        try
        {
            foreach (var site in await _client.ListSitesAsync(SelectedServer.Id) ?? []) Sites.Add(site);
            SiteStatusText = Sites.Count == 0 ? "尚未创建 RemoteOS 管理的站点。" : $"共 {Sites.Count} 个受管站点。";
        }
        catch (Exception exception) { SiteStatusText = $"无法加载站点：{exception.Message}"; }
    }

    private async Task ReportSiteSaveErrorAsync(string message)
    {
        SiteStatusText = message;
        if (ShowSiteSaveErrorAsync is not null) await ShowSiteSaveErrorAsync(message);
    }

    [RelayCommand]
    private void AddSiteBinding() => SiteBindings.Add(new WebServerSiteBindingEditor());

    [RelayCommand]
    private void RemoveSiteBinding(WebServerSiteBindingEditor? binding)
    {
        if (binding is not null && SiteBindings.Count > 1) SiteBindings.Remove(binding);
    }

    /// <summary>Converts pasted <c>domain:port</c> rows into independently editable binding pairs.</summary>
    [RelayCommand]
    private void GenerateSiteBindings()
    {
        var generated = SiteBindingsBatch
            .Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseSiteBinding)
            .ToArray();
        if (generated.Length == 0) return;
        SiteBindings.Clear();
        foreach (var binding in generated) SiteBindings.Add(binding);
    }

    public async Task OpenSiteDirectoryAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && OpenFileBrowserAtPathAsync is not null)
            await OpenFileBrowserAtPathAsync(path);
    }

    private static WebServerSiteBindingEditor ParseSiteBinding(string value)
    {
        var domain = value;
        var port = 80;
        var separator = value.LastIndexOf(':');
        // Bracketed IPv6 can be pasted as [2001:db8::1]:5000. Bare IPv6 remains a domain
        // value and is subsequently validated by the server without guessing a port.
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var parsedPort)
            && (value[0] != '[' || value.IndexOf(']') < separator))
        {
            domain = value[..separator].Trim('[', ']');
            port = parsedPort;
        }
        return new WebServerSiteBindingEditor(domain, port);
    }

    private async Task RunOperationAsync(string kindKey, WebServerDto server, Func<string, CancellationToken, Task<WebServerOperationDto?>> start)
        => await RunOperationAsync(kindKey, ct => start(server.Id, ct));

    private async Task RunOperationAsync(string kindKey, Func<CancellationToken, Task<WebServerOperationDto?>> start, bool rethrowApiException = false)
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
        catch (WebServerApiException) when (rethrowApiException)
        {
            throw;
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
    private bool CanRefreshWindowsVersions => IsWindowsServer && HasReadPermission && !IsLoading && !IsOperationRunning;
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
    private bool CanSaveSite => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.ManagementMode is WebServerManagementMode.Integrated or WebServerManagementMode.Managed;
    private bool CanOpenSiteEditor => CanSaveSite;
    private bool CanEditSite => CanSaveSite && SelectedSite is not null;
    private bool CanDeleteSite => CanSaveSite && SelectedSite is not null;
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
        ("install", "installing_package") => LocalizedText.Get("webservers.operation.stage.installing_package", "正在通过系统软件源安装 Nginx"),
        ("install", "downloading") => LocalizedText.Get("webservers.operation.stage.downloading", "正在从 Nginx 官方站点下载"),
        ("install", "extracting") => LocalizedText.Get("webservers.operation.stage.extracting", "正在验证并解压 Nginx 包"),
        ("install", "removing_existing_installation") => LocalizedText.Get("webservers.operation.stage.removing_existing_installation", "正在删除现有 Nginx 安装"),
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
        "webserver.install_unsupported_platform" => LocalizedText.Get("webservers.problem.install_unsupported_platform", "当前平台不支持内置安装；请配置受管安装程序。"),
        "webserver.version_invalid" => LocalizedText.Get("webservers.problem.version_invalid", "请输入 Nginx 版本号，例如 1.31.3。"),
        "webserver.version_required" => LocalizedText.Get("webservers.problem.version_required", "请选择或输入 Nginx 版本，或选择本地 ZIP 包。"),
        "webserver.version_catalog_unavailable" => LocalizedText.Get("webservers.version_catalog.unavailable", "无法加载 Nginx Windows 版本列表。请刷新后重试，或选择本地 ZIP 包。"),
        "webserver.download_failed" => LocalizedText.Get("webservers.problem.download_failed", "无法从 Nginx 官方站点下载该版本。"),
        "webserver.managed_installation_exists" => LocalizedText.Get("webservers.problem.managed_installation_exists", "RemoteOS 的 Nginx 安装目录已存在。"),
        "webserver.existing_installation_unsafe" => LocalizedText.Get("webservers.problem.existing_installation_unsafe", "现有安装目录不完整或包含不安全链接，无法复用或删除。"),
        "webserver.package_invalid" => LocalizedText.Get("webservers.problem.package_invalid", "Nginx ZIP 包无效或不符合预期布局。"),
        "webserver.package_not_found" => LocalizedText.Get("webservers.problem.package_not_found", "离线安装包已过期，请重新选择。"),
        _ => problemCode,
    };

    private static string SiteSaveProblemText(string problemCode) => problemCode switch
    {
        "webserver.site_name_invalid" => "站点名称不能为空，且最多 80 个字符；仅使用字母、数字、空格、连字符或下划线。",
        "webserver.site_kind_invalid" => "请选择有效的站点类型。",
        "webserver.site_port_invalid" => "HTTP 端口必须介于 1 和 65535 之间。",
        "webserver.site_server_name_required" => "请至少填写一个域名或 IP 地址。",
        "webserver.site_server_name_invalid" => "域名或 IP 地址格式无效。请逐行填写，例如 app.example.com 或 192.168.1.20。",
        "webserver.site_already_exists" => "已存在同名站点。请关闭此对话框，在列表中选择该站点后进行编辑；新建操作不会覆盖已有站点。",
        "webserver.site_binding_conflict" => "该站点的某个域名/IP 与监听端口已由另一个站点使用。当前站点的所有域名都会监听所有填写的端口，请调整域名或端口。",
        "webserver.site_conflict" => "站点与现有配置冲突，但服务器未提供具体冲突项。请检查站点名称、域名/IP 和监听端口后重试。",
        "webserver.site_certificate_required" => "启用 HTTPS 时必须选择一个可用证书。使用 IP 时通常应先关闭 HTTPS。",
        "webserver.site_upstream_invalid" => "代理地址必须是完整的 HTTP 或 HTTPS 地址，例如 http://127.0.0.1:3000。",
        "webserver.site_elevation_required" => "RemoteOS Server 未以管理员权限运行，无法写入和应用 Nginx 站点配置。请查看服务端 WebServer 日志了解详情。",
        "webserver.site_config_test_failed" => "新站点配置未通过 Nginx 校验。请检查域名、代理地址、证书路径，以及现有 Nginx 配置。",
        "webserver.site_reload_failed" => "站点配置已通过校验，但 Nginx 重载失败。请检查监听端口、Nginx 运行状态和服务端权限。",
        "webserver.site_save_failed" => "Nginx 无法保存此站点配置。请确认 Nginx 已集成，且 RemoteOS Server 具有配置目录的写入权限。",
        _ => $"保存站点失败：{ProblemText(problemCode)}",
    };

    // nginx -t + reload is fast; a tighter poll keeps the UI responsive without spamming the host.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
}

/// <summary>Editable, UI-local representation of one domain and HTTP-port pair.</summary>
public sealed partial class WebServerSiteBindingEditor : ObservableObject
{
    public WebServerSiteBindingEditor(string domain = "", int port = 80)
    {
        Domain = domain;
        Port = port;
    }

    [ObservableProperty] private string _domain = string.Empty;
    [ObservableProperty] private int _port = 80;
}
