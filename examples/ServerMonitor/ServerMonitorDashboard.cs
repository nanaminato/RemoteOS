using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteOS.AppSDK;
using RemoteRect = RemoteOS.Core.Primitives.Rect;

namespace RemoteOS.Examples.ServerMonitor;

/// <summary>Owns one dashboard window, including its refresh loop and view state.</summary>
public sealed class ServerMonitorDashboard
{
    private static readonly Color CpuColor = Color.Parse("#55B6FF");
    private static readonly Color MemoryColor = Color.Parse("#B98CFF");
    private static readonly Color NetworkColor = Color.Parse("#40D6A1");

    private readonly IExternalAppContext _context;
    private readonly MonitorSettingsStore _settingsStore;
    private MonitorSettings _settings;
    private readonly MetricHistory _cpuHistory = new();
    private readonly MetricHistory _memoryHistory = new();
    private readonly MetricHistory _networkHistory = new();
    private readonly HistoryChart _cpuChart = new(CpuColor);
    private readonly HistoryChart _memoryChart = new(MemoryColor);
    private readonly HistoryChart _networkChart = new(NetworkColor, 1024 * 1024);
    private readonly TextBlock _status = MutedText("Connecting to RemoteOS server metrics…");
    private readonly TextBlock _updated = MutedText("Waiting for the first sample");
    private readonly TextBlock _cpuValue = BigValue("—");
    private readonly TextBlock _memoryValue = BigValue("—");
    private readonly TextBlock _networkValue = BigValue("—");
    private readonly TextBlock _uptime = MutedText("Uptime: —");
    private readonly TextBlock _coreUsage = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
    private readonly TextBlock _disks = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
    private readonly TextBlock _networks = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
    private readonly TextBlock _gpus = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
    private IExternalAppWindowHandle? _window;
    private CancellationTokenSource? _sampling;
    private ComboBox? _intervalPicker;
    private ComboBox? _historyPicker;

    public ServerMonitorDashboard(IExternalAppContext context, MonitorSettings settings, MonitorSettingsStore settingsStore)
    {
        _context = context;
        _settings = settings.Normalize();
        _settingsStore = settingsStore;
    }

    public void Show()
    {
        var tabs = new TabControl
        {
            Margin = new Thickness(20, 0, 20, 20),
            ItemsSource = new[]
            {
                new TabItem { Header = "Performance", Content = CreatePerformanceView() },
                new TabItem { Header = "Settings", Content = CreateSettingsView() },
            },
        };
        var root = new DockPanel { Background = new SolidColorBrush(Color.Parse("#0D1420")) };
        var header = CreateHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(tabs);

        _window = _context.Windows.ShowWindow("Server Monitor", root, new RemoteRect(120, 80, 1080, 760), "📊");
        _window.Closed.Register(() => _sampling?.Cancel());
        RestartSampling();
    }

    private Control CreateHeader()
    {
        var refresh = new Button { Content = "Refresh now", VerticalAlignment = VerticalAlignment.Center };
        refresh.Click += async (_, _) => await RefreshOnceAsync(_window?.Closed ?? CancellationToken.None);
        var header = new Grid
        {
            Margin = new Thickness(20, 18, 20, 12),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = "Server Monitor", FontSize = 25, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = "Live server performance and resource usage", Opacity = 0.68 },
                    },
                },
                refresh,
            },
        };
        Grid.SetColumn(refresh, 1);
        return header;
    }

    private Control CreatePerformanceView()
    {
        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        var cpu = MetricCard("CPU", _cpuValue, _uptime, CpuColor);
        var memory = MetricCard("Memory", _memoryValue, MutedText("Physical memory in use"), MemoryColor);
        var network = MetricCard("Network", _networkValue, MutedText("Total send + receive"), NetworkColor);
        summary.Children.Add(cpu);
        summary.Children.Add(memory);
        summary.Children.Add(network);
        Grid.SetColumn(memory, 1);
        Grid.SetColumn(network, 2);

        var charts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        var cpuAndMemory = new StackPanel { Spacing = 12, Children = { ChartCard("CPU utilization", "Last samples", _cpuChart), ChartCard("Memory utilization", "Last samples", _memoryChart) } };
        var networkAndDetails = new StackPanel { Spacing = 12, Children = { ChartCard("Network throughput", "Scale: 1 MB/s", _networkChart), DetailCard("CPU cores", _coreUsage) } };
        charts.Children.Add(cpuAndMemory);
        charts.Children.Add(networkAndDetails);
        Grid.SetColumn(networkAndDetails, 1);

        var resources = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        var disks = DetailCard("Disks", _disks);
        var networkDetails = new StackPanel { Spacing = 12, Children = { DetailCard("Network adapters", _networks), DetailCard("GPU", _gpus) } };
        resources.Children.Add(disks);
        resources.Children.Add(networkDetails);
        Grid.SetColumn(networkDetails, 1);

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 14,
                Children = { _status, summary, charts, resources, _updated },
            },
        };
    }

    private Control CreateSettingsView()
    {
        _intervalPicker = new ComboBox
        {
            ItemsSource = RefreshOptions,
            SelectedItem = RefreshOptions.First(option => option.Milliseconds == _settings.RefreshIntervalMilliseconds),
            MinWidth = 220,
        };
        _intervalPicker.SelectionChanged += (_, _) => ApplySettingsFromControls();
        _historyPicker = new ComboBox
        {
            ItemsSource = HistoryOptions,
            SelectedItem = HistoryOptions.First(option => option.Samples == _settings.HistoryLength),
            MinWidth = 220,
        };
        _historyPicker.SelectionChanged += (_, _) => ApplySettingsFromControls();
        var reset = new Button { Content = "Clear chart history", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            _cpuHistory.Clear();
            _memoryHistory.Clear();
            _networkHistory.Clear();
            UpdateCharts();
        };

        return new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(4, 12),
            Children =
            {
                new TextBlock { Text = "Monitoring settings", FontSize = 20, FontWeight = FontWeight.SemiBold },
                MutedText("These preferences are saved locally for this Server Monitor package."),
                SettingRow("Refresh frequency", "The RemoteOS host enforces a minimum interval of one second.", _intervalPicker),
                SettingRow("Chart history", "How many completed samples each chart keeps in memory.", _historyPicker),
                reset,
            },
        };
    }

    private async void RestartSampling()
    {
        _sampling?.Cancel();
        _sampling?.Dispose();
        _sampling = CancellationTokenSource.CreateLinkedTokenSource(_window?.Closed ?? CancellationToken.None);
        var cancellationToken = _sampling.Token;
        try
        {
            await foreach (var result in _context.ServerMonitor.WatchAsync(TimeSpan.FromMilliseconds(_settings.RefreshIntervalMilliseconds), cancellationToken))
                await Dispatcher.UIThread.InvokeAsync(() => ApplyResult(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _context.ServerMonitor.GetSnapshotAsync(cancellationToken);
            ApplyResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void ApplySettingsFromControls()
    {
        if (_intervalPicker?.SelectedItem is not RefreshOption interval || _historyPicker?.SelectedItem is not HistoryOption history)
            return;
        var updated = new MonitorSettings(interval.Milliseconds, history.Samples).Normalize();
        if (updated == _settings)
            return;
        var restart = updated.RefreshIntervalMilliseconds != _settings.RefreshIntervalMilliseconds;
        _settings = updated;
        _settingsStore.Save(updated);
        if (restart)
            RestartSampling();
    }

    private void ApplyResult(ServerMetricsResult result)
    {
        if (result.Status == AppCapabilityResult.PermissionDenied)
        {
            _status.Text = "Permission denied. Grant ‘Read server performance metrics’ in Settings → Applications → Server Monitor.";
            return;
        }
        if (result.Status != AppCapabilityResult.Succeeded || result.Snapshot is null)
        {
            _status.Text = "Server metrics are currently unavailable.";
            return;
        }

        var snapshot = result.Snapshot;
        var send = snapshot.Networks.Sum(network => network.SendRateBytesPerSecond);
        var receive = snapshot.Networks.Sum(network => network.ReceiveRateBytesPerSecond);
        var totalNetwork = send + receive;
        _cpuHistory.Add(snapshot.CpuPercent, _settings.HistoryLength);
        _memoryHistory.Add(snapshot.MemoryPercent, _settings.HistoryLength);
        _networkHistory.Add(totalNetwork, _settings.HistoryLength);
        UpdateCharts();

        _status.Text = "Live server metrics";
        _cpuValue.Text = $"{snapshot.CpuPercent:0.0}%";
        _memoryValue.Text = $"{snapshot.MemoryPercent:0.0}%";
        _networkValue.Text = $"{FormatRate(totalNetwork)}";
        _uptime.Text = $"{snapshot.CpuCoreCount} cores · Uptime {FormatUptime(snapshot.UptimeSeconds)}";
        _coreUsage.Text = snapshot.CpuPerCorePercent.Count == 0
            ? "Per-core data is unavailable."
            : string.Join("   ", snapshot.CpuPerCorePercent.Select((value, index) => $"CPU {index}: {value:0}%"));
        _disks.Text = snapshot.Disks.Count == 0
            ? "No disk data available."
            : string.Join(Environment.NewLine, snapshot.Disks.Select(disk =>
                $"{disk.Name}   {disk.Percent:0}% used   {FormatBytes(disk.UsedBytes)} / {FormatBytes(disk.TotalBytes)}"));
        _networks.Text = snapshot.Networks.Count == 0
            ? "No network adapter data available."
            : string.Join(Environment.NewLine, snapshot.Networks.Select(network =>
                $"{network.Name}   ↓ {FormatRate(network.ReceiveRateBytesPerSecond)}   ↑ {FormatRate(network.SendRateBytesPerSecond)}"));
        _gpus.Text = snapshot.Gpus.Count == 0
            ? "No GPU data available."
            : string.Join(Environment.NewLine, snapshot.Gpus.Select(gpu =>
                $"{gpu.Name}   {(gpu.UsagePercent is { } usage ? $"{usage:0}%" : "usage unavailable")}" +
                (gpu.TemperatureCelsius is { } temp ? $"   {temp:0}°C" : string.Empty)));
        _updated.Text = $"Updated {snapshot.Timestamp.LocalDateTime:HH:mm:ss} · refreshing every {_settings.RefreshIntervalMilliseconds / 1000.0:0.#} s";
    }

    private void UpdateCharts()
    {
        _cpuChart.Update(_cpuHistory.Values);
        _memoryChart.Update(_memoryHistory.Values);
        _networkChart.Update(_networkHistory.Values);
    }

    private static Border MetricCard(string title, Control value, Control detail, Color accent) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#151D2B")),
        BorderBrush = new SolidColorBrush(accent),
        BorderThickness = new Thickness(1, 1, 1, 1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14),
        Child = new StackPanel { Spacing = 5, Children = { MutedText(title), value, detail } },
    };

    private static Border ChartCard(string title, string subtitle, Control chart) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#101927")),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12),
        Child = new StackPanel { Spacing = 7, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, MutedText(subtitle), chart } },
    };

    private static Border DetailCard(string title, Control content) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#101927")),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14),
        Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, content } },
    };

    private static Grid SettingRow(string title, string detail, Control editor)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
        var text = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, MutedText(detail) } };
        row.Children.Add(text);
        row.Children.Add(editor);
        Grid.SetColumn(editor, 1);
        return row;
    }

    private static TextBlock BigValue(string value) => new() { Text = value, FontSize = 26, FontWeight = FontWeight.SemiBold };
    private static TextBlock MutedText(string value) => new() { Text = value, Opacity = 0.68, TextWrapping = TextWrapping.Wrap };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(value, 0);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }

    private static string FormatRate(long value) => $"{FormatBytes(value)}/s";
    private static string FormatUptime(long seconds) => TimeSpan.FromSeconds(Math.Max(seconds, 0)).ToString(@"d\.hh\:mm\:ss");

    private sealed record RefreshOption(string Label, int Milliseconds)
    {
        public override string ToString() => Label;
    }

    private sealed record HistoryOption(string Label, int Samples)
    {
        public override string ToString() => Label;
    }

    private static readonly RefreshOption[] RefreshOptions =
    [
        new("Every 1 second (fastest)", 1000),
        new("Every 2 seconds", 2000),
        new("Every 5 seconds", 5000),
        new("Every 10 seconds", 10000),
    ];

    private static readonly HistoryOption[] HistoryOptions =
    [
        new("30 samples", 30),
        new("60 samples", 60),
        new("120 samples", 120),
        new("240 samples", 240),
    ];
}
