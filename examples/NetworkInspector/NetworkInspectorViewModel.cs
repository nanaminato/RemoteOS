using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using RemoteOS.AppSDK;

namespace RemoteOS.Examples.NetworkInspector;

internal sealed class NetworkInspectorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly INetworkDiagnostics _diagnostics;
    private readonly ISystemLanguage _language;
    private readonly ObservableCollection<NetworkDiagnosticEntry> _all = new();
    private string _filter = string.Empty;
    private NetworkDiagnosticEntry? _selectedEntry;

    public NetworkInspectorViewModel(INetworkDiagnostics diagnostics, ISystemLanguage language)
    {
        _diagnostics = diagnostics;
        _language = language;
        foreach (var entry in diagnostics.GetSnapshot().Entries)
            _all.Add(entry);
        _diagnostics.EntryCompleted += OnEntryCompleted;
        _diagnostics.StateChanged += OnStateChanged;
        _language.LanguageChanged += OnLanguageChanged;
        ToggleRecordingCommand = new AsyncCommand(ToggleRecordingAsync);
        ClearCommand = new AsyncCommand(ClearAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title => Text("title", "Network Inspector");
    public string RecordButtonText => _diagnostics.State.IsRecording ? Text("stop", "Stop") : Text("record", "Record");
    public string ClearText => Text("clear", "Clear");
    public string FilterPlaceholder => Text("filter", "Filter");
    public string Status => _diagnostics.State.IsAvailable
        ? $"{_all.Count} {Text("requests", "requests")}" : (_diagnostics.State.UnavailableReason ?? Text("unavailable", "Unavailable"));
    public AsyncCommand ToggleRecordingCommand { get; }
    public AsyncCommand ClearCommand { get; }

    public string Filter
    {
        get => _filter;
        set { if (_filter == value) return; _filter = value; OnPropertyChanged(); OnPropertyChanged(nameof(VisibleEntries)); }
    }

    public IReadOnlyList<NetworkDiagnosticEntry> VisibleEntries => _all.Where(entry => string.IsNullOrWhiteSpace(Filter)
        || entry.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || entry.PathAndQuery.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || entry.Source.Contains(Filter, StringComparison.OrdinalIgnoreCase)).Reverse().ToArray();

    public NetworkDiagnosticEntry? SelectedEntry
    {
        get => _selectedEntry;
        set { if (_selectedEntry == value) return; _selectedEntry = value; OnPropertyChanged(); OnPropertyChanged(nameof(Details)); }
    }

    public string Details => SelectedEntry is not { } entry ? Text("select", "Select a request to view its summary.")
        : $"{entry.Kind} · {entry.Outcome}\n{entry.Method ?? "—"} {entry.PathAndQuery}\n{Text("status_label", "Status")}: {entry.StatusCode?.ToString() ?? "—"}\n{Text("duration", "Duration")}: {entry.Duration.TotalMilliseconds:0} ms\n{Text("source", "Source")}: {entry.Source}\n{Text("type", "Type")}: {entry.ContentType ?? "—"}\n{Text("size", "Size")}: {entry.DeclaredContentLength?.ToString() ?? "—"}\n{Text("error", "Error")}: {entry.ErrorKind ?? "—"}";

    public void Dispose()
    {
        _diagnostics.EntryCompleted -= OnEntryCompleted;
        _diagnostics.StateChanged -= OnStateChanged;
        _language.LanguageChanged -= OnLanguageChanged;
    }

    private async Task ToggleRecordingAsync()
    {
        if (_diagnostics.State.IsRecording)
            await _diagnostics.StopRecordingAsync();
        else
            await _diagnostics.StartRecordingAsync();
    }

    private async Task ClearAsync()
    {
        await _diagnostics.ClearAsync();
        _all.Clear();
        SelectedEntry = null;
        OnPropertyChanged(nameof(VisibleEntries));
        OnPropertyChanged(nameof(Status));
    }

    private void OnEntryCompleted(object? sender, NetworkDiagnosticEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        _all.Add(entry);
        if (_all.Count > 500) _all.RemoveAt(0);
        OnPropertyChanged(nameof(VisibleEntries));
        OnPropertyChanged(nameof(Status));
    });

    private void OnStateChanged(object? sender, NetworkDiagnosticsState state) => Dispatcher.UIThread.Post(() =>
    {
        if (!state.IsAvailable) _all.Clear();
        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(VisibleEntries));
    });

    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(ClearText));
        OnPropertyChanged(nameof(FilterPlaceholder));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Details));
    });

    private string Text(string key, string fallback) => Strings.TryGetValue(_language.CurrentLanguage, out var localized)
        && localized.TryGetValue(key, out var value) ? value : fallback;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Strings = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["zh-CN"] = new Dictionary<string, string> { ["title"] = "网络检查器", ["record"] = "开始录制", ["stop"] = "停止", ["clear"] = "清空", ["filter"] = "筛选", ["requests"] = "条请求", ["unavailable"] = "不可用", ["select"] = "选择一条请求以查看摘要。", ["status_label"] = "状态", ["duration"] = "耗时", ["source"] = "来源", ["type"] = "类型", ["size"] = "大小", ["error"] = "错误" },
        ["ja-JP"] = new Dictionary<string, string> { ["title"] = "ネットワーク インスペクター", ["record"] = "記録開始", ["stop"] = "停止", ["clear"] = "クリア", ["filter"] = "フィルター", ["requests"] = "件のリクエスト", ["unavailable"] = "利用できません", ["select"] = "リクエストを選択して概要を表示します。", ["status_label"] = "状態", ["duration"] = "時間", ["source"] = "ソース", ["type"] = "種類", ["size"] = "サイズ", ["error"] = "エラー" },
    };
}

internal sealed class AsyncCommand(Func<Task> execute) : System.Windows.Input.ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
