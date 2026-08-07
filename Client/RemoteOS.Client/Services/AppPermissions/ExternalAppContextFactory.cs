using Client.Apps.Settings;
using Client.Apps.TaskManager;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Protocol.SystemMonitor;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;
using CoreAppPermissions = RemoteOS.Core.Applications.AppPermissions;

namespace Client.Services.AppPermissions;

/// <summary>Creates the capability-only context used by the future package loader.</summary>
public sealed class ExternalAppContextFactory
{
    private readonly IAppPermissionManager _permissions;
    private readonly ShellSettings _settings;
    private readonly ISettingsClient _settingsClient;
    private readonly IAuthSession _session;
    private readonly DefaultAppRegistry _defaultApps;
    private readonly IWindowManager _windowManager;
    private readonly ITaskManagerClient _systemMonitor;
    private readonly ISettingsNavigation _settingsNavigation;

    public ExternalAppContextFactory(
        IAppPermissionManager permissions,
        ShellSettings settings,
        ISettingsClient settingsClient,
        IAuthSession session,
        DefaultAppRegistry defaultApps,
        IWindowManager windowManager,
        ITaskManagerClient systemMonitor,
        ISettingsNavigation settingsNavigation)
    {
        _permissions = permissions;
        _settings = settings;
        _settingsClient = settingsClient;
        _session = session;
        _defaultApps = defaultApps;
        _windowManager = windowManager;
        _systemMonitor = systemMonitor;
        _settingsNavigation = settingsNavigation;
    }

    public IExternalAppContext Create(AppId appId) => new ExternalAppContext(
        appId,
        new AppPermissionScope(appId, _permissions),
        new DesktopAppearanceCapability(appId, _permissions, _settings, _settingsClient, _session, _defaultApps),
        new ServerMonitorCapability(appId, _permissions, _systemMonitor),
        _settingsNavigation,
        new ExternalAppWindowService(appId, _windowManager));

    private sealed record ExternalAppContext(
        AppId AppId,
        IAppPermissionScope Permissions,
        IDesktopAppearance DesktopAppearance,
        IServerMonitor ServerMonitor,
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
}
