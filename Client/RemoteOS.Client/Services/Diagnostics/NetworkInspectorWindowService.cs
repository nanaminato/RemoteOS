using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Client.Services;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;

namespace Client.Services.Diagnostics;

/// <summary>Owns the host-level Network Inspector window; it is not an application package.</summary>
public sealed class NetworkInspectorWindowService : IDisposable
{
    private readonly IWindowManager _windows;
    private readonly NetworkDiagnosticsService _diagnostics;
    private readonly LocalizationService _localization;
    private ManagedWindow? _window;
    private NetworkInspectorView? _view;

    public NetworkInspectorWindowService(IWindowManager windows, NetworkDiagnosticsService diagnostics,
        LocalizationService localization)
    {
        _windows = windows;
        _diagnostics = diagnostics;
        _localization = localization;
        _windows.WindowClosed += OnWindowClosed;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public bool CanOpen => _diagnostics.State.IsAvailable;

    public void Open()
    {
        if (!CanOpen)
            return;

        if (_window is not null && _windows.Windows.Contains(_window))
        {
            if (_window.State == RemoteOS.Core.Windows.WindowState.Minimized)
                _windows.Restore(_window);
            _windows.Focus(_window);
            return;
        }

        _view = new NetworkInspectorView(_diagnostics, _localization);
        _window = _windows.Create(new WindowCreateOptions(
            new AppId("remoteos.system.network-inspector"), Title(), _view,
            new Rect(110, 70, 1180, 720), "🔎"));
    }

    public void Dispose()
    {
        _windows.WindowClosed -= OnWindowClosed;
        _localization.LanguageChanged -= OnLanguageChanged;
        _view?.Dispose();
    }

    private void OnWindowClosed(object? sender, ManagedWindow window)
    {
        if (!ReferenceEquals(window, _window))
            return;
        _view?.Dispose();
        _view = null;
        _window = null;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (_window is not null)
            _window.Title = Title();
    }

    private string Title() => _localization.Get("network_inspector.title", "Network Inspector");
}

/// <summary>Small Chrome-like system diagnostics view. All strings are refreshed by its owner.</summary>
internal sealed class NetworkInspectorView : UserControl, IDisposable
{
    private readonly NetworkDiagnosticsService _diagnostics;
    private readonly LocalizationService _localization;
    private readonly ObservableCollection<string> _rows = new();
    private readonly ListBox _list = new();
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _record = new() { MinWidth = 104 };
    private readonly Button _clear = new() { MinWidth = 80 };
    private readonly TextBox _filter = new() { Width = 280 };
    private IReadOnlyList<NetworkDiagnosticEntry> _visible = Array.Empty<NetworkDiagnosticEntry>();

    public NetworkInspectorView(NetworkDiagnosticsService diagnostics, LocalizationService localization)
    {
        _diagnostics = diagnostics;
        _localization = localization;
        Build();
        _diagnostics.EntryCompleted += OnEntryCompleted;
        _diagnostics.StateChanged += OnStateChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        Refresh();
    }

    public void Dispose()
    {
        _diagnostics.EntryCompleted -= OnEntryCompleted;
        _diagnostics.StateChanged -= OnStateChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void Build()
    {
        _record.Click += async (_, _) =>
        {
            if (_diagnostics.State.IsRecording) await _diagnostics.StopRecordingAsync();
            else await _diagnostics.StartRecordingAsync();
            Refresh();
        };
        _clear.Click += async (_, _) => { await _diagnostics.ClearAsync(); Refresh(); };
        _filter.TextChanged += (_, _) => Refresh();
        _list.ItemsSource = _rows;
        _list.SelectionChanged += (_, _) => ShowDetails(_list.SelectedIndex);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(16, 14, 16, 10) };
        toolbar.Children.Add(_record);
        toolbar.Children.Add(_clear);
        toolbar.Children.Add(_filter);
        toolbar.Children.Add(_status);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(toolbar);
        grid.Children.Add(_list);
        grid.Children.Add(_details);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_details, 2);
        _list.Margin = new Avalonia.Thickness(16, 0, 16, 8);
        _details.Margin = new Avalonia.Thickness(16, 0, 16, 16);
        _details.MinHeight = 170;
        Content = grid;
    }

    private void Refresh()
    {
        var snapshot = _diagnostics.GetSnapshot(new NetworkDiagnosticsQuery(Text: _filter.Text));
        _visible = snapshot.Entries.Reverse().ToArray();
        _rows.Clear();
        foreach (var entry in _visible)
            _rows.Add($"{entry.StartedAt:HH:mm:ss.fff}  {entry.Kind,-7}  {entry.Method ?? "—",-6}  {entry.Outcome,-14}  {entry.StatusCode?.ToString() ?? "—",-4}  {entry.Duration.TotalMilliseconds,6:0} ms  {entry.PathAndQuery}");
        _record.Content = snapshot.State.IsRecording ? T("network_inspector.stop", "Stop") : T("network_inspector.record", "Record");
        _clear.Content = T("network_inspector.clear", "Clear");
        _filter.PlaceholderText = T("network_inspector.filter", "Filter");
        _status.Text = snapshot.State.IsAvailable
            ? $"{_visible.Count} {T("network_inspector.requests", "requests")}" : snapshot.State.UnavailableReason;
        if (_list.SelectedIndex >= _visible.Count)
            _list.SelectedIndex = -1;
        ShowDetails(_list.SelectedIndex);
    }

    private void ShowDetails(int index)
    {
        if (index < 0 || index >= _visible.Count)
        {
            _details.Text = T("network_inspector.select", "Select a request to view its summary.");
            return;
        }
        var entry = _visible[index];
        _details.Text = $"{entry.Kind} · {entry.Outcome}\n{entry.Method ?? "—"} {entry.PathAndQuery}\n"
            + $"{T("network_inspector.status", "Status")}: {entry.StatusCode?.ToString() ?? "—"}\n"
            + $"{T("network_inspector.duration", "Duration")}: {entry.Duration.TotalMilliseconds:0} ms\n"
            + $"{T("network_inspector.source", "Source")}: {entry.Source}\n"
            + $"{T("network_inspector.type", "Type")}: {entry.ContentType ?? "—"}\n"
            + $"{T("network_inspector.size", "Size")}: {entry.DeclaredContentLength?.ToString() ?? "—"}\n"
            + $"{T("network_inspector.error", "Error")}: {entry.ErrorKind ?? "—"}";
    }

    private void OnEntryCompleted(object? sender, NetworkDiagnosticEntry entry) => Dispatcher.UIThread.Post(Refresh);
    private void OnStateChanged(object? sender, NetworkDiagnosticsState state) => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(Refresh);
    private string T(string key, string fallback) => _localization.Get(key, fallback);
}
