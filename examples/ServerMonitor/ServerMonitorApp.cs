using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteRect = RemoteOS.Core.Primitives.Rect;

namespace RemoteOS.Examples.ServerMonitor;

/// <summary>
/// A third-party-style application: it receives no server URL, token, or Task Manager client.
/// It observes only the permission-gated IServerMonitor capability exposed by RemoteOS.
/// </summary>
public sealed class ServerMonitorApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(
        new AppId("com.remoteos.example.server-monitor"),
        "Server Monitor",
        "0.1.0-dev",
        "📈",
        "Example third-party server resource monitor",
        [AppPermissions.ServerMetricsRead]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        var status = new TextBlock { Text = "Connecting to RemoteOS server metrics…", Opacity = 0.7 };
        var cpu = Metric("CPU", "—");
        var memory = Metric("Memory", "—");
        var network = Metric("Network", "—");
        var disks = new TextBlock { Text = "Disks: —", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var refreshed = new TextBlock { Text = "Waiting for the first sample…", FontSize = 12, Opacity = 0.6 };
        var refresh = new Button { Content = "Refresh now", HorizontalAlignment = HorizontalAlignment.Left };

        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Server Monitor", FontSize = 22, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = "This development app uses the RemoteOS server.metrics.read capability.", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                status,
                cpu,
                memory,
                network,
                disks,
                refresh,
                refreshed,
            },
        };

        var handle = context.Windows.ShowWindow(
            "Server Monitor",
            content,
            new RemoteRect(160, 110, 500, 420),
            "📈");

        refresh.Click += async (_, _) =>
        {
            var result = await context.ServerMonitor.GetSnapshotAsync(handle.Closed);
            ApplyResult(result, status, cpu, memory, network, disks, refreshed);
        };
        _ = ObserveAsync(context, handle.Closed, status, cpu, memory, network, disks, refreshed);
        return Task.CompletedTask;
    }

    private static async Task ObserveAsync(
        IExternalAppContext context,
        CancellationToken closed,
        TextBlock status,
        TextBlock cpu,
        TextBlock memory,
        TextBlock network,
        TextBlock disks,
        TextBlock refreshed)
    {
        try
        {
            await foreach (var result in context.ServerMonitor.WatchAsync(TimeSpan.FromSeconds(2), closed))
                Dispatcher.UIThread.Post(() => ApplyResult(result, status, cpu, memory, network, disks, refreshed));
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
    }

    private static void ApplyResult(
        ServerMetricsResult result,
        TextBlock status,
        TextBlock cpu,
        TextBlock memory,
        TextBlock network,
        TextBlock disks,
        TextBlock refreshed)
    {
        if (result.Status == AppCapabilityResult.PermissionDenied)
        {
            status.Text = "Permission denied. Grant ‘读取服务器性能指标’ in Settings → Applications → Application permissions.";
            return;
        }
        if (result.Status != AppCapabilityResult.Succeeded || result.Snapshot is null)
        {
            status.Text = "Server metrics are currently unavailable.";
            return;
        }

        var snapshot = result.Snapshot;
        status.Text = "Live server metrics";
        cpu.Text = $"CPU: {snapshot.CpuPercent:0.0}%  ·  {snapshot.CpuCoreCount} cores";
        memory.Text = $"Memory: {snapshot.MemoryPercent:0.0}%  ·  {FormatBytes(snapshot.MemoryUsedBytes)} / {FormatBytes(snapshot.MemoryTotalBytes)}";
        var send = snapshot.Networks.Sum(item => item.SendRateBytesPerSecond);
        var receive = snapshot.Networks.Sum(item => item.ReceiveRateBytesPerSecond);
        network.Text = $"Network: ↑ {FormatBytes(send)}/s  ↓ {FormatBytes(receive)}/s";
        disks.Text = snapshot.Disks.Count == 0
            ? "Disks: no disk data"
            : "Disks: " + string.Join("  ·  ", snapshot.Disks.Select(disk => $"{disk.Name} {disk.Percent:0}%"));
        refreshed.Text = $"Updated {snapshot.Timestamp.LocalDateTime:HH:mm:ss}";
    }

    private static TextBlock Metric(string label, string value) => new() { Text = $"{label}: {value}", FontSize = 16 };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(value, 0);
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.#} {units[index]}";
    }
}
