using Client.Apps.Settings;
using Client.Apps.TaskManager;
using Client.Apps.Explorer;
using Client.Services.Auth;
using Client.Services.Diagnostics;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Protocol.SystemMonitor;
using RemoteOS.Protocol.Capabilities;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;
using CoreAppPermissions = RemoteOS.Core.Applications.AppPermissions;

namespace Client.Services.AppPermissions;

/// <summary>Creates the capability-only context used by the future package loader.</summary>
public sealed class ExternalAppContextFactory
{
    private readonly IAppPermissionManager _permissions;
    private readonly ISystemLanguage _systemLanguage;
    private readonly ShellSettings _settings;
    private readonly ISettingsClient _settingsClient;
    private readonly IAuthSession _session;
    private readonly DefaultAppRegistry _defaultApps;
    private readonly IWindowManager _windowManager;
    private readonly ITaskManagerClient _systemMonitor;
    private readonly IExplorerClient _files;
    private readonly ISettingsNavigation _settingsNavigation;
    private readonly IAppCapabilityClient _capabilities;
    private readonly NetworkDiagnosticsService _networkDiagnostics;

    public ExternalAppContextFactory(
        IAppPermissionManager permissions,
        ISystemLanguage systemLanguage,
        ShellSettings settings,
        ISettingsClient settingsClient,
        IAuthSession session,
        DefaultAppRegistry defaultApps,
        IWindowManager windowManager,
        ITaskManagerClient systemMonitor,
        IExplorerClient files,
        ISettingsNavigation settingsNavigation,
        IAppCapabilityClient capabilities,
        NetworkDiagnosticsService networkDiagnostics)
    {
        _permissions = permissions;
        _systemLanguage = systemLanguage;
        _settings = settings;
        _settingsClient = settingsClient;
        _session = session;
        _defaultApps = defaultApps;
        _windowManager = windowManager;
        _systemMonitor = systemMonitor;
        _files = files;
        _settingsNavigation = settingsNavigation;
        _capabilities = capabilities;
        _networkDiagnostics = networkDiagnostics;
    }

    public IExternalAppContext Create(AppId appId) => new ExternalAppContext(
        appId,
        new AppPermissionScope(appId, _permissions),
        new DesktopAppearanceCapability(appId, _permissions, _settings, _settingsClient, _session, _defaultApps),
        new ServerMonitorCapability(appId, _permissions, _systemMonitor),
        new ServerFilesCapability(appId, _permissions, _files),
        new ExternalFileApiAccess(appId, _permissions, _session, _capabilities),
        new ExternalMediaService(appId, _permissions, _session, _capabilities),
        new NetworkDiagnosticsCapability(appId, _permissions, _networkDiagnostics),
        _systemLanguage,
        _settingsNavigation,
        new ExternalAppWindowService(appId, _windowManager));

    private sealed record ExternalAppContext(
        AppId AppId,
        IAppPermissionScope Permissions,
        IDesktopAppearance DesktopAppearance,
        IServerMonitor ServerMonitor,
        IServerFiles ServerFiles,
        IExternalFileApiAccess FileApi,
        IExternalMediaService Media,
        INetworkDiagnostics NetworkDiagnostics,
        ISystemLanguage SystemLanguage,
        ISettingsNavigation Settings,
        IExternalAppWindowService Windows) : IExternalAppContext;

    private sealed class ExternalAppWindowService(AppId appId, IWindowManager windowManager) : IExternalAppWindowService
    {
        public IExternalAppWindowHandle ShowWindow(
            string title,
            Avalonia.Controls.Control content,
            Rect? bounds = null,
            string? iconGlyph = null,
            bool canResize = true,
            bool canMinimize = true,
            bool canMaximize = true)
        {
            var window = windowManager.Create(new WindowCreateOptions(
                OwnerAppId: appId,
                Title: title,
                Content: content,
                Bounds: bounds,
                IconGlyph: iconGlyph,
                CanResize: canResize,
                CanMinimize: canMinimize,
                CanMaximize: canMaximize));
            return new ExternalAppWindowHandle(window, windowManager);
        }
    }

    private sealed class ExternalAppWindowHandle : IExternalAppWindowHandle
    {
        private readonly IWindowManager _windowManager;
        private readonly EventHandler<ManagedWindow> _closedHandler;
        private readonly CancellationTokenSource _closed = new();

        public ExternalAppWindowHandle(ManagedWindow window, IWindowManager windowManager)
        {
            Window = window;
            _windowManager = windowManager;
            _closedHandler = (_, closedWindow) =>
            {
                if (!ReferenceEquals(closedWindow, Window)) return;
                _windowManager.WindowClosed -= _closedHandler;
                _closed.Cancel();
                _closed.Dispose();
            };
            _windowManager.WindowClosed += _closedHandler;
        }

        public ManagedWindow Window { get; }
        public CancellationToken Closed => _closed.Token;
        public bool IsFullScreen => Window.IsFullScreen;

        public void EnterFullScreen() => _windowManager.EnterFullScreen(Window);

        public void ExitFullScreen() => _windowManager.ExitFullScreen(Window);
    }

    private sealed class NetworkDiagnosticsCapability : INetworkDiagnostics
    {
        private readonly AppId _appId;
        private readonly IAppPermissionManager _permissions;
        private readonly NetworkDiagnosticsService _diagnostics;

        public NetworkDiagnosticsCapability(AppId appId, IAppPermissionManager permissions, NetworkDiagnosticsService diagnostics)
        {
            _appId = appId;
            _permissions = permissions;
            _diagnostics = diagnostics;
            _diagnostics.StateChanged += (_, state) =>
            {
                if (HasPermission()) StateChanged?.Invoke(this, state);
            };
            _diagnostics.EntryCompleted += (_, entry) =>
            {
                if (HasPermission()) EntryCompleted?.Invoke(this, entry);
            };
        }

        public NetworkDiagnosticsState State => HasPermission()
            ? _diagnostics.State
            : new NetworkDiagnosticsState(false, false, "Network diagnostics permission is not granted.");

        public event EventHandler<NetworkDiagnosticsState>? StateChanged;
        public event EventHandler<NetworkDiagnosticEntry>? EntryCompleted;

        public NetworkDiagnosticsSnapshot GetSnapshot(NetworkDiagnosticsQuery? query = null) => HasPermission()
            ? _diagnostics.GetSnapshot(query)
            : new NetworkDiagnosticsSnapshot(State, Array.Empty<NetworkDiagnosticEntry>(), 0);

        public Task<NetworkDiagnosticsCommandResult> StartRecordingAsync(CancellationToken cancellationToken = default) => HasPermission()
            ? _diagnostics.StartRecordingAsync(cancellationToken)
            : Task.FromResult(NetworkDiagnosticsCommandResult.PermissionDenied);

        public Task StopRecordingAsync(CancellationToken cancellationToken = default) => HasPermission()
            ? _diagnostics.StopRecordingAsync(cancellationToken)
            : Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => HasPermission()
            ? _diagnostics.ClearAsync(cancellationToken)
            : Task.CompletedTask;

        private bool HasPermission() => _permissions.IsGranted(_appId, CoreAppPermissions.DiagnosticsNetworkRead);
    }

    private sealed class ExternalFileApiAccess(
        AppId appId,
        IAppPermissionManager permissions,
        IAuthSession session,
        IAppCapabilityClient capabilities) : IExternalFileApiAccess
    {
        public async Task<FileApiAccessResult> GetAccessAsync(CancellationToken cancellationToken = default)
        {
            var scopes = new List<string>();
            if (permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
            {
                scopes.Add(FileCapabilityScopes.List);
                scopes.Add(FileCapabilityScopes.Read);
            }
            if (permissions.IsGranted(appId, CoreAppPermissions.ServerFilesWrite))
            {
                scopes.Add(FileCapabilityScopes.Write);
                scopes.Add(FileCapabilityScopes.Manage);
            }
            if (scopes.Count == 0)
                return new FileApiAccessResult(AppCapabilityResult.PermissionDenied, null, null, null);
            if (session.ServerUrl is null)
                return new FileApiAccessResult(AppCapabilityResult.Unavailable, null, null, null);

            try
            {
                var token = await capabilities.IssueFileTokenAsync(appId.Value, scopes, cancellationToken);
                return new FileApiAccessResult(AppCapabilityResult.Succeeded,
                    new Uri(session.ServerUrl, UriKind.Absolute), token.AccessToken, token.ExpiresAt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new FileApiAccessResult(AppCapabilityResult.Unavailable, null, null, null);
            }
        }
    }

    private sealed class ExternalMediaService(
        AppId appId,
        IAppPermissionManager permissions,
        IAuthSession session,
        IAppCapabilityClient capabilities) : IExternalMediaService
    {
        public async Task<ExternalMediaLeaseResult> OpenPlaybackAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
                return new ExternalMediaLeaseResult(AppCapabilityResult.PermissionDenied, null);
            if (string.IsNullOrWhiteSpace(path))
                return new ExternalMediaLeaseResult(AppCapabilityResult.InvalidArgument, null);
            if (session.ServerUrl is null)
                return new ExternalMediaLeaseResult(AppCapabilityResult.Unavailable, null);

            try
            {
                var created = await capabilities.CreateMediaLeaseAsync(appId.Value, path, cancellationToken);
                var playbackUri = new Uri(new Uri(session.ServerUrl, UriKind.Absolute),
                    AppCapabilityRoutes.MediaStream(created.LeaseId).TrimStart('/'));
                return new ExternalMediaLeaseResult(AppCapabilityResult.Succeeded,
                    new HostMediaLease(playbackUri, created, capabilities,
                        () => permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                var detail = exception.StatusCode switch
                {
                    System.Net.HttpStatusCode.NotFound => "The file no longer exists on the server.",
                    System.Net.HttpStatusCode.Forbidden => "The server denied access to the file.",
                    System.Net.HttpStatusCode.Unauthorized => "Your RemoteOS session has expired.",
                    System.Net.HttpStatusCode.BadRequest => exception.Message,
                    { } statusCode => $"The server rejected the playback lease request (HTTP {(int)statusCode}).",
                    _ => "The server could not create a playback lease.",
                };
                return new ExternalMediaLeaseResult(AppCapabilityResult.Unavailable, null, detail);
            }
            catch (Exception)
            {
                return new ExternalMediaLeaseResult(AppCapabilityResult.Unavailable, null,
                    "The RemoteOS server could not be reached.");
            }
        }
    }

    private sealed class HostMediaLease : IExternalMediaLease
    {
        private readonly IAppCapabilityClient _capabilities;
        private readonly Func<bool> _canRenew;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _renewal;
        private readonly object _gate = new();
        private readonly string _leaseId;
        private DateTimeOffset _expiresAt;
        private bool _disposed;

        public HostMediaLease(Uri playbackUri, MediaLeaseDto created, IAppCapabilityClient capabilities, Func<bool> canRenew)
        {
            PlaybackUri = playbackUri;
            _leaseId = created.LeaseId;
            _expiresAt = created.ExpiresAt;
            _capabilities = capabilities;
            _canRenew = canRenew;
            _renewal = RenewUntilDisposedAsync();
        }

        public Uri PlaybackUri { get; }
        public DateTimeOffset ExpiresAt
        {
            get { lock (_gate) return _expiresAt; }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            await _shutdown.CancelAsync();
            try { await _renewal; }
            catch (OperationCanceledException) { }
            try { await _capabilities.RevokeMediaLeaseAsync(_leaseId); }
            catch { /* The short server-side expiry is the fallback revocation path. */ }
            _shutdown.Dispose();
        }

        private async Task RenewUntilDisposedAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), _shutdown.Token);
                    if (!_canRenew())
                    {
                        try { await _capabilities.RevokeMediaLeaseAsync(_leaseId, _shutdown.Token); }
                        catch { /* The short lease expiry remains the fallback. */ }
                        return;
                    }
                    try
                    {
                        var renewed = await _capabilities.RenewMediaLeaseAsync(_leaseId, _shutdown.Token);
                        lock (_gate)
                            _expiresAt = renewed.ExpiresAt;
                    }
                    catch when (!_shutdown.IsCancellationRequested && ExpiresAt > DateTimeOffset.UtcNow)
                    {
                        // A short network interruption should not immediately end an otherwise active player.
                        await Task.Delay(TimeSpan.FromSeconds(10), _shutdown.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // The owner has disposed the media lease.
            }
            catch
            {
                // Renewal failed after the lease expired or the host session became unavailable.
            }
        }
    }

    private sealed class AppPermissionScope(AppId appId, IAppPermissionManager permissions) : IAppPermissionScope
    {
        public bool IsGranted(string permissionId) => permissions.IsGranted(appId, permissionId);
    }

    private sealed class DesktopAppearanceCapability(
        AppId appId,
        IAppPermissionManager permissions,
        ShellSettings settings,
        ISettingsClient settingsClient,
        IAuthSession session,
        DefaultAppRegistry defaultApps) : IDesktopAppearance
    {
        public async Task<AppCapabilityResult> SetWallpaperAsync(string wallpaperKey, CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.DesktopWallpaperWrite))
                return AppCapabilityResult.PermissionDenied;

            if (!settings.TrySetWallpaperKey(wallpaperKey))
                return AppCapabilityResult.InvalidArgument;

            if (session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
                return AppCapabilityResult.Succeeded;

            try
            {
                await settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id,
                    settings.ToPreferences(defaultApps.Snapshot), cancellationToken);
                return AppCapabilityResult.Succeeded;
            }
            catch
            {
                return AppCapabilityResult.Unavailable;
            }
        }
    }

    private sealed class ServerMonitorCapability(
        AppId appId,
        IAppPermissionManager permissions,
        ITaskManagerClient systemMonitor) : IServerMonitor
    {
        public async Task<ServerMetricsResult> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerMetricsRead))
                return new ServerMetricsResult(AppCapabilityResult.PermissionDenied, null);

            try
            {
                var metrics = await systemMonitor.GetMetricsAsync(cancellationToken);
                return new ServerMetricsResult(AppCapabilityResult.Succeeded, ToSnapshot(metrics));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new ServerMetricsResult(AppCapabilityResult.Unavailable, null);
            }
        }

        public async IAsyncEnumerable<ServerMetricsResult> WatchAsync(TimeSpan? interval = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var cadence = interval ?? TimeSpan.FromSeconds(2);
            cadence = TimeSpan.FromMilliseconds(Math.Clamp(cadence.TotalMilliseconds, 1000, 60000));
            while (!cancellationToken.IsCancellationRequested)
            {
                yield return await GetSnapshotAsync(cancellationToken);
                await Task.Delay(cadence, cancellationToken);
            }
        }

        private static ServerMetricsSnapshot ToSnapshot(SystemMetricsDto metrics) => new(
            metrics.Timestamp,
            metrics.Cpu.TotalPercent,
            metrics.Cpu.CoreCount,
            metrics.Cpu.PerCorePercent,
            metrics.Memory.TotalBytes,
            metrics.Memory.UsedBytes,
            metrics.Memory.AvailableBytes,
            metrics.Memory.Percent,
            metrics.Disks.Select(disk => new ServerDiskMetric(disk.Name, disk.TotalBytes, disk.UsedBytes, disk.FreeBytes, disk.Percent)).ToArray(),
            metrics.Networks.Select(network => new ServerNetworkMetric(network.Name, network.SendRateBytesPerSec, network.ReceiveRateBytesPerSec)).ToArray(),
            metrics.Gpus.Select(gpu => new ServerGpuMetric(gpu.Name, gpu.UsagePercent, gpu.MemoryTotalBytes, gpu.MemoryUsedBytes, gpu.TemperatureCelsius)).ToArray(),
            metrics.UptimeSeconds);
    }

    private sealed class ServerFilesCapability(
        AppId appId,
        IAppPermissionManager permissions,
        IExplorerClient files) : IServerFiles
    {
        public async Task<ServerFileReadResult> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
                return new ServerFileReadResult(AppCapabilityResult.PermissionDenied, null, null);
            if (string.IsNullOrWhiteSpace(path))
                return new ServerFileReadResult(AppCapabilityResult.InvalidArgument, null, null);

            try
            {
                var result = await files.DownloadAsync(path, cancellationToken);
                return result is { } download
                    ? new ServerFileReadResult(AppCapabilityResult.Succeeded, download.Stream, download.FileName)
                    : new ServerFileReadResult(AppCapabilityResult.InvalidArgument, null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new ServerFileReadResult(AppCapabilityResult.Unavailable, null, null);
            }
        }
    }
}
