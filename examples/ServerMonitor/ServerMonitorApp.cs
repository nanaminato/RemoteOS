using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Examples.ServerMonitor.Services;
using RemoteOS.Examples.ServerMonitor.ViewModels;
using RemoteOS.Examples.ServerMonitor.Views;
using RemoteRect = RemoteOS.Core.Primitives.Rect;

namespace RemoteOS.Examples.ServerMonitor;

/// <summary>
/// Composition root for the Server Monitor development application.
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
        var viewModel = new ServerMonitorViewModel(context.ServerMonitor, settingsStore.Load(), settingsStore);
        var view = new ServerMonitorMainView { DataContext = viewModel };
        var window = context.Windows.ShowWindow("Server Monitor", view,
            new RemoteRect(120, 80, 1080, 760), Manifest.IconGlyph);
        _ = viewModel.StartAsync(window.Closed);
        return Task.CompletedTask;
    }
}
