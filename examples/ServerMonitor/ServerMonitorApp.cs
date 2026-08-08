using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;

namespace RemoteOS.Examples.ServerMonitor;

/// <summary>
/// Entry point for the Server Monitor development application.  The dashboard, its sampling
/// lifecycle, and persisted preferences deliberately live outside this composition root.
/// </summary>
public sealed class ServerMonitorApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(
        new AppId("com.remoteos.example.server-monitor"),
        "Server Monitor",
        "0.2.0-dev",
        "📊",
        "A lightweight performance dashboard for a RemoteOS server",
        [AppPermissions.ServerMetricsRead]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        var settingsStore = new MonitorSettingsStore();
        var dashboard = new ServerMonitorDashboard(context, settingsStore.Load(), settingsStore);
        dashboard.Show();
        return Task.CompletedTask;
    }
}
