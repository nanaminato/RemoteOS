using System.Collections.ObjectModel;
using Avalonia.Threading;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.SystemMonitor;

namespace Client.Apps.TaskManager.ViewModels;

/// <summary>性能数据来自服务端统一采样器与 SignalR；进程列表按需分页查询，不能随性能图表刷新。</summary>
public sealed partial class TaskManagerViewModel : ObservableObject, IAsyncDisposable
{
    private const int HistoryLimit = 60;
    private readonly ITaskManagerClient _client;
    private readonly PerformanceStream _stream;
    private readonly DispatcherTimer _processTimer;
    private List<ProcessInfoDto> _allProcesses = [];
    private int _refreshingProcesses;
    private long _lastSequence;
    private int _disposed;

    public TaskManagerViewModel(ITaskManagerClient client, PerformanceStream stream)
    {
        _client = client;
        _stream = stream;
        _stream.SnapshotReceived += OnSnapshotReceived;
        _stream.Reconnected += OnStreamReconnected;
        _stream.Disconnected += OnStreamDisconnected;
        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _processTimer.Tick += async (_, _) => await RefreshProcessesAsync();
        FilteredProcesses = [];
        CpuHistory = [];
        MemoryHistory = [];
    }

    public ObservableCollection<ProcessInfoDto> FilteredProcesses { get; }
    public ObservableCollection<double> CpuHistory { get; }
    public ObservableCollection<double> MemoryHistory { get; }

    [ObservableProperty] private PerformanceInfoDto? _info;
    [ObservableProperty] private PerformanceRealtimeSnapshotDto? _snapshot;
    [ObservableProperty] private ProcessInfoDto? _selectedProcess;
    [ObservableProperty] private bool _isAutoRefresh = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = LocalizedText.Get("task_manager.status.collecting");
    [ObservableProperty] private string _connectionStatus = "正在初始化性能采样…";
    [ObservableProperty] private string _killFeedback = string.Empty;
    [ObservableProperty] private TaskManagerTab _activeTab = TaskManagerTab.Performance;
    [ObservableProperty] private string _processFilter = string.Empty;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private int _processTotalCount;

    public Action? CloseAction { get; set; }

    public async Task StartAsync()
    {
        await RefreshPerformanceAsync();
        try
        {
            await _stream.StartAsync();
            ConnectionStatus = "实时连接";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "实时连接不可用，已使用快照";
            StatusText = LocalizedText.Format("task_manager.status.collect_failed", ex.Message);
        }
    }

    public void Stop() => _ = DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _processTimer.Stop();
        _stream.SnapshotReceived -= OnSnapshotReceived;
        _stream.Reconnected -= OnStreamReconnected;
        _stream.Disconnected -= OnStreamDisconnected;
        await _stream.DisposeAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (ActiveTab == TaskManagerTab.Processes) await RefreshProcessesAsync();
        else await RefreshPerformanceAsync();
    }

    [RelayCommand(CanExecute = nameof(CanKill))]
    private async Task KillProcessAsync()
    {
        var process = SelectedProcess;
        if (process is null) return;
        KillFeedback = LocalizedText.Format("task_manager.process.terminating", process.Name, process.Id);
        try
        {
            var result = await _client.KillProcessAsync(process.Id, force: false);
            if (result.Success)
            {
                KillFeedback = LocalizedText.Format("task_manager.process.terminated", process.Name, process.Id);
                SelectedProcess = null;
            }
            else if (result.RequiresElevation)
                KillFeedback = LocalizedText.Format("task_manager.process.elevation_required", process.Name, process.Id, result.Error);
            else
                KillFeedback = LocalizedText.Format("task_manager.process.termination_failed", result.Error);
            await RefreshProcessesAsync();
        }
        catch (Exception ex) { KillFeedback = LocalizedText.Format("task_manager.process.termination_failed", ex.Message); }
    }

    private bool CanKill => SelectedProcess is not null;
    partial void OnSelectedProcessChanged(ProcessInfoDto? value) => KillProcessCommand.NotifyCanExecuteChanged();

    [RelayCommand] private void SwitchToPerformance() => ActiveTab = TaskManagerTab.Performance;
    [RelayCommand] private void SwitchToProcesses() => ActiveTab = TaskManagerTab.Processes;
    [RelayCommand] private void Close() => CloseAction?.Invoke();
    [RelayCommand] private void ClearFilter() => ProcessFilter = string.Empty;

    partial void OnActiveTabChanged(TaskManagerTab value)
    {
        if (value == TaskManagerTab.Processes)
        {
            _ = RefreshProcessesAsync();
            if (IsAutoRefresh) _processTimer.Start();
        }
        else _processTimer.Stop();
    }

    partial void OnIsAutoRefreshChanged(bool value)
    {
        if (value && ActiveTab == TaskManagerTab.Processes && !_processTimer.IsEnabled) _processTimer.Start();
        else if (!value && _processTimer.IsEnabled) _processTimer.Stop();
    }

    partial void OnProcessFilterChanged(string value) => ApplyFilter();

    private async Task RefreshPerformanceAsync()
    {
        try
        {
            var infoTask = _client.GetPerformanceInfoAsync();
            var historyTask = _client.GetPerformanceHistoryAsync();
            await Task.WhenAll(infoTask, historyTask);
            Info = await infoTask;
            var history = await historyTask;
            if (history.Count == 0)
            {
                try { ApplySnapshot(await _client.GetPerformanceSnapshotAsync(), resetHistory: true); }
                catch { ConnectionStatus = "等待首个有效样本"; }
            }
            else ReplaceHistory(history);
            if (ConnectionStatus != "实时连接") ConnectionStatus = "快照已更新";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "性能数据不可用";
            StatusText = LocalizedText.Format("task_manager.status.collect_failed", ex.Message);
        }
    }

    private async Task RefreshProcessesAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshingProcesses, 1, 0) != 0) return;
        IsLoading = true;
        try
        {
            var page = await _client.QueryProcessesAsync(filter: string.IsNullOrWhiteSpace(ProcessFilter) ? null : ProcessFilter, sort: "memory");
            ProcessTotalCount = page.TotalCount;
            UpdateProcesses(page.Items);
            StatusText = LocalizedText.Format("task_manager.status.updated", page.SampledAt.LocalDateTime,
                Snapshot?.Cpu.TotalPercent ?? 0, page.TotalCount);
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("task_manager.status.collect_failed", ex.Message); }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _refreshingProcesses, 0);
        }
    }

    private void OnSnapshotReceived(PerformanceRealtimeSnapshotDto snapshot)
        => Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot, resetHistory: false));

    private void OnStreamReconnected()
        => Dispatcher.UIThread.Post(async () =>
        {
            ConnectionStatus = "已重连，正在补齐历史";
            await RefreshPerformanceAsync();
            ConnectionStatus = "实时连接";
        });

    private void OnStreamDisconnected()
        => Dispatcher.UIThread.Post(() => ConnectionStatus = "实时连接已断开，保留最后快照");

    private void ReplaceHistory(IReadOnlyList<PerformanceRealtimeSnapshotDto> history)
    {
        CpuHistory.Clear();
        MemoryHistory.Clear();
        _lastSequence = 0;
        foreach (var snapshot in history.OrderBy(x => x.Sequence)) ApplySnapshot(snapshot, resetHistory: false);
    }

    private void ApplySnapshot(PerformanceRealtimeSnapshotDto snapshot, bool resetHistory)
    {
        if (!resetHistory && snapshot.Sequence <= _lastSequence) return;
        if (resetHistory)
        {
            CpuHistory.Clear();
            MemoryHistory.Clear();
            _lastSequence = 0;
        }
        _lastSequence = Math.Max(_lastSequence, snapshot.Sequence);
        Snapshot = snapshot;
        MemoryPercent = snapshot.Memory.TotalBytes <= 0 ? 0 : Math.Round(snapshot.Memory.UsedBytes * 100d / snapshot.Memory.TotalBytes, 1);
        AppendHistory(CpuHistory, snapshot.Cpu.TotalPercent);
        AppendHistory(MemoryHistory, MemoryPercent);
        StatusText = LocalizedText.Format("task_manager.status.updated", snapshot.Timestamp.LocalDateTime,
            snapshot.Cpu.TotalPercent, ProcessTotalCount);
    }

    private void UpdateProcesses(IReadOnlyList<ProcessInfoDto> processes)
    {
        var selected = SelectedProcess;
        _allProcesses = processes.ToList();
        RebuildFilteredProcesses(selected);
    }

    private void ApplyFilter() => RebuildFilteredProcesses(SelectedProcess);

    private void RebuildFilteredProcesses(ProcessInfoDto? selected)
    {
        var filter = ProcessFilter.Trim();
        IEnumerable<ProcessInfoDto> source = _allProcesses;
        if (!string.IsNullOrWhiteSpace(filter))
            source = source.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (p.UserName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        FilteredProcesses.Clear();
        foreach (var process in source) FilteredProcesses.Add(process);
        SelectedProcess = selected is null ? null : FilteredProcesses.FirstOrDefault(p => p.Id == selected.Id && p.StartTime == selected.StartTime);
    }

    private static void AppendHistory(ObservableCollection<double> history, double value)
    {
        history.Add(Math.Clamp(value, 0, 100));
        while (history.Count > HistoryLimit) history.RemoveAt(0);
    }
}

public enum TaskManagerTab { Performance, Processes }
