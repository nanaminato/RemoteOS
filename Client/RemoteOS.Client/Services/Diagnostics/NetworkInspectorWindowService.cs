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
    private readonly TextBox _summary = CreateReadOnlyTextBox();
    private readonly TextBox _requestHeaders = CreateReadOnlyTextBox();
    private readonly TextBox _requestBody = CreateReadOnlyTextBox();
    private readonly TextBox _responseHeaders = CreateReadOnlyTextBox();
    private readonly TextBox _responseBody = CreateReadOnlyTextBox();
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
        var details = new TabControl { Margin = new Avalonia.Thickness(16, 0, 16, 16) };
        details.Items.Add(new TabItem { Header = T("network_inspector.summary", "Summary"), Content = _summary });
        details.Items.Add(new TabItem
        {
            Header = T("network_inspector.request", "Request"),
            Content = CreatePayloadPanel(_requestHeaders, _requestBody)
        });
        details.Items.Add(new TabItem
        {
            Header = T("network_inspector.response", "Response"),
            Content = CreatePayloadPanel(_responseHeaders, _responseBody)
        });

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,280") };
        grid.Children.Add(toolbar);
        grid.Children.Add(_list);
        grid.Children.Add(details);
        Grid.SetRow(_list, 1);
        Grid.SetRow(details, 2);
        _list.Margin = new Avalonia.Thickness(16, 0, 16, 8);
        Content = grid;
    }

    private void Refresh()
    {
        // Requests are shown in capture order so each new one appears at the end.
        // Preserve the selected request by its stable diagnostic ID instead of retaining a shifting index.
        var selectedIndexBeforeRefresh = _list.SelectedIndex;
        var selectedId = selectedIndexBeforeRefresh >= 0 && selectedIndexBeforeRefresh < _visible.Count
            ? _visible[selectedIndexBeforeRefresh].Id
            : (long?)null;
        var snapshot = _diagnostics.GetSnapshot(new NetworkDiagnosticsQuery(Text: _filter.Text));
        _visible = snapshot.Entries;
        _rows.Clear();
        foreach (var entry in _visible)
            _rows.Add($"{entry.StartedAt:HH:mm:ss.fff}  {entry.Kind,-7}  {entry.Method ?? "—",-6}  {entry.Outcome,-14}  {entry.StatusCode?.ToString() ?? "—",-4}  {entry.Duration.TotalMilliseconds,6:0} ms  {entry.PathAndQuery}");
        _record.Content = snapshot.State.IsRecording ? T("network_inspector.stop", "Stop") : T("network_inspector.record", "Record");
        _clear.Content = T("network_inspector.clear", "Clear");
        _filter.PlaceholderText = T("network_inspector.filter", "Filter");
        _status.Text = snapshot.State.IsAvailable
            ? $"{_visible.Count} {T("network_inspector.requests", "requests")}" : snapshot.State.UnavailableReason;

        var selectedIndex = selectedId is long id
            ? Array.FindIndex(_visible.ToArray(), entry => entry.Id == id)
            : -1;
        _list.SelectedIndex = selectedIndex;
        ShowDetails(selectedIndex);
    }

    private void ShowDetails(int index)
    {
        if (index < 0 || index >= _visible.Count)
        {
            _summary.Text = T("network_inspector.select", "Select a request to view its summary.");
            _requestHeaders.Text = string.Empty;
            _requestBody.Text = string.Empty;
            _responseHeaders.Text = string.Empty;
            _responseBody.Text = string.Empty;
            return;
        }
        var entry = _visible[index];
        _summary.Text = $"{entry.Kind} · {entry.Outcome}\n{entry.Method ?? "—"} {entry.PathAndQuery}\n"
            + $"{T("network_inspector.status", "Status")}: {entry.StatusCode?.ToString() ?? "—"}\n"
            + $"{T("network_inspector.duration", "Duration")}: {entry.Duration.TotalMilliseconds:0} ms\n"
            + $"{T("network_inspector.source", "Source")}: {entry.Source}\n"
            + $"{T("network_inspector.type", "Type")}: {entry.ContentType ?? "—"}\n"
            + $"{T("network_inspector.size", "Size")}: {entry.DeclaredContentLength?.ToString() ?? "—"}\n"
            + $"{T("network_inspector.error", "Error")}: {entry.ErrorKind ?? "—"}";
        _requestHeaders.Text = FormatHeaders(entry.RequestHeaders);
        _requestBody.Text = FormatPayload(entry.RequestBody, "No request body.");
        _responseHeaders.Text = FormatHeaders(entry.ResponseHeaders);
        _responseBody.Text = FormatPayload(entry.ResponseBody, "No response body.");
    }

    private static TextBox CreateReadOnlyTextBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };

    private Grid CreatePayloadPanel(TextBox headers, TextBox body)
    {
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,*") };
        var headersLabel = new TextBlock { Text = T("network_inspector.headers", "Headers"), Margin = new Avalonia.Thickness(8, 4, 8, 2) };
        var bodyLabel = new TextBlock { Text = T("network_inspector.body", "Body"), Margin = new Avalonia.Thickness(8, 6, 8, 2) };
        headers.Margin = new Avalonia.Thickness(8, 0, 8, 0);
        body.Margin = new Avalonia.Thickness(8, 0, 8, 8);
        panel.Children.Add(headersLabel);
        panel.Children.Add(headers);
        panel.Children.Add(bodyLabel);
        panel.Children.Add(body);
        Grid.SetRow(headers, 1);
        Grid.SetRow(bodyLabel, 2);
        Grid.SetRow(body, 3);
        return panel;
    }

    private static string FormatHeaders(IReadOnlyDictionary<string, string>? headers) => headers is null || headers.Count == 0
        ? "No headers."
        : string.Join(Environment.NewLine, headers.Select(header => $"{header.Key}: {header.Value}"));

    private static string FormatPayload(NetworkDiagnosticPayload? payload, string emptyMessage) => payload is null
        ? emptyMessage
        : $"{payload.Format}\n\n{payload.Content}";

    private void OnEntryCompleted(object? sender, NetworkDiagnosticEntry entry) => Dispatcher.UIThread.Post(Refresh);
    private void OnStateChanged(object? sender, NetworkDiagnosticsState state) => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(Refresh);
    private string T(string key, string fallback) => _localization.Get(key, fallback);
}
