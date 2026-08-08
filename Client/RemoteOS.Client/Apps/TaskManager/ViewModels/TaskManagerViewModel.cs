using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.SystemMonitor;

namespace Client.Apps.TaskManager.ViewModels;

/// <summary>任务管理器主视图模型。
///
/// 数据流：
/// - <see cref="DispatcherTimer"/>（默认 2s）触发 <see cref="RefreshAsync"/>，并行拉取 metrics + processes
/// - 性能标签页绑定 <see cref="Metrics"/> 子属性（CPU/内存/磁盘/网络/GPU），CPU/内存柱状图绑定 <see cref="CpuHistory"/>/<see cref="MemoryHistory"/>
/// - 进程标签页绑定 <see cref="FilteredProcesses"/>（按 <see cref="ProcessFilter"/> 过滤），选中后"结束任务"调用 <see cref="KillProcessCommand"/>
/// - 结束进程权限不足时服务端返回 RequiresElevation=true（RemoteOS 不自动提权，提示用户在宿主 OS 提权）
///
/// 服务端以宿主 OS 进程身份采集（复用宿主用户/权限，不另建 ACL）。</summary>
public sealed partial class TaskManagerViewModel : ObservableObject
{
    private readonly ITaskManagerClient _client;
    private readonly DispatcherTimer _timer;
    private int _refreshing; // Interlocked 重入保护
    private List<ProcessInfoDto> _allProcesses = new();
    private const int HistoryLimit = 60;

    public TaskManagerViewModel(ITaskManagerClient client)
    {
        _client = client;
        FilteredProcesses = new ObservableCollection<ProcessInfoDto>();
        CpuHistory = new ObservableCollection<double>();
        MemoryHistory = new ObservableCollection<double>();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>当前可见进程（按过滤词过滤后）。</summary>
    public ObservableCollection<ProcessInfoDto> FilteredProcesses { get; }

    [ObservableProperty] private SystemMetricsDto? _metrics;
    [ObservableProperty] private ProcessInfoDto? _selectedProcess;
    [ObservableProperty] private bool _isAutoRefresh = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = LocalizedText.Get("task_manager.status.collecting");
    [ObservableProperty] private string _killFeedback = string.Empty;
    [ObservableProperty] private TaskManagerTab _activeTab = TaskManagerTab.Performance;
    [ObservableProperty] private string _processFilter = string.Empty;
    [ObservableProperty] private bool _hasGpu;

    /// <summary>CPU 占用历史（0-100），用于实时柱状图，上限 60 个采样。</summary>
    public ObservableCollection<double> CpuHistory { get; }
    /// <summary>内存占用历史（0-100），用于实时柱状图。</summary>
    public ObservableCollection<double> MemoryHistory { get; }

    /// <summary>GPU 不可用时的提示文案。</summary>
    public string GpuHint => LocalizedText.Get("task_manager.gpu_unavailable");

    /// <summary>关闭窗口回调（由 TaskManagerApp 注入）。关闭即停止刷新。</summary>
    public Action? CloseAction { get; set; }

    /// <summary>由 TaskManagerApp 在窗口打开后调用：立即采集一次并启动定时器。</summary>
    public async Task StartAsync()
    {
        await RefreshAsync();
        if (IsAutoRefresh) _timer.Start();
    }

    /// <summary>停止定时刷新（窗口关闭/隐藏时调用）。</summary>
    public void Stop() => _timer.Stop();

    private async void OnTimerTick(object? sender, EventArgs e) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        IsLoading = true;
        try
        {
            var metricsTask = _client.GetMetricsAsync();
            var procsTask = _client.ListProcessesAsync();
            await Task.WhenAll(metricsTask, procsTask);

            var metrics = await metricsTask;
            var procs = await procsTask;

            Metrics = metrics;
            UpdateCharts(metrics);
            HasGpu = metrics.Gpus.Count > 0;

            UpdateProcesses(procs);
            StatusText = $"已更新 — {DateTime.Now:HH:mm:ss}　CPU {metrics.Cpu.TotalPercent:0.0}%　进程 {procs.Count}";
        }
        catch (Exception ex)
        {
            StatusText = $"采集失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [RelayCommand(CanExecute = nameof(CanKill))]
    private async Task KillProcessAsync()
    {
        var proc = SelectedProcess;
        if (proc is null) return;
        KillFeedback = $"正在结束进程 {proc.Name} (PID {proc.Id})...";
        try
        {
            var result = await _client.KillProcessAsync(proc.Id, force: false);
            if (result.Success)
            {
                KillFeedback = $"已结束进程 {proc.Name} (PID {proc.Id})。";
                SelectedProcess = null;
            }
            else if (result.RequiresElevation)
            {
                KillFeedback = $"权限不足，无法结束 {proc.Name} (PID {proc.Id})。{result.Error}";
            }
            else
            {
                KillFeedback = $"结束进程失败：{result.Error}";
            }
            // 立即刷新进程列表
            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            KillFeedback = $"结束进程失败：{ex.Message}";
        }
    }

    private bool CanKill => SelectedProcess is not null;

    partial void OnSelectedProcessChanged(ProcessInfoDto? value)
        => KillProcessCommand.NotifyCanExecuteChanged();

    // IsAutoRefresh 由 CheckBox 双向绑定驱动；变化时启停定时器（避免 Command + IsChecked 双触发）
    partial void OnIsAutoRefreshChanged(bool value)
    {
        if (value && !_timer.IsEnabled) _timer.Start();
        else if (!value && _timer.IsEnabled) _timer.Stop();
    }

    [RelayCommand]
    private void SwitchToPerformance() => ActiveTab = TaskManagerTab.Performance;

    [RelayCommand]
    private void SwitchToProcesses() => ActiveTab = TaskManagerTab.Processes;

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();

    partial void OnProcessFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearFilter() => ProcessFilter = string.Empty;

    private async Task RefreshProcessesAsync()
    {
        try
        {
            var procs = await _client.ListProcessesAsync();
            UpdateProcesses(procs);
        }
        catch { /* 忽略，定时刷新会重试 */ }
    }

    private void ApplyFilter()
    {
        var filter = ProcessFilter?.Trim() ?? string.Empty;
        IEnumerable<ProcessInfoDto> source = _allProcesses;
        if (!string.IsNullOrEmpty(filter))
        {
            source = _allProcesses.Where(p =>
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (p.UserName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        FilteredProcesses.Clear();
        foreach (var p in source) FilteredProcesses.Add(p);

        // Do not leave a selectable process that has been filtered out.
        if (SelectedProcess is not null && !FilteredProcesses.Contains(SelectedProcess))
            SelectedProcess = null;
    }

    private void UpdateProcesses(IReadOnlyList<ProcessInfoDto> processes)
    {
        var selected = SelectedProcess;
        _allProcesses = processes.ToList();

        // A refresh creates new DTO instances. Keep the selection only when it still
        // identifies the same running process in the latest snapshot.
        if (selected is not null)
        {
            SelectedProcess = _allProcesses.FirstOrDefault(p =>
                p.Id == selected.Id && p.StartTime == selected.StartTime);
        }

        ApplyFilter();
    }

    private void UpdateCharts(SystemMetricsDto metrics)
    {
        AppendHistory(CpuHistory, metrics.Cpu.TotalPercent);
        AppendHistory(MemoryHistory, metrics.Memory.Percent);
    }

    private static void AppendHistory(ObservableCollection<double> history, double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        history.Add(clamped);
        while (history.Count > HistoryLimit) history.RemoveAt(0);
    }
}

/// <summary>任务管理器标签页。</summary>
public enum TaskManagerTab
{
    Performance,
    Processes,
}
