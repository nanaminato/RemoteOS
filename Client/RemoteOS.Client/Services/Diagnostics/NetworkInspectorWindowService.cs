using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Data;
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

/// <summary>A compact Chrome Network-style view for the host diagnostics recorder.</summary>
internal sealed class NetworkInspectorView : UserControl, IDisposable
{
    private readonly NetworkDiagnosticsService _diagnostics;
    private readonly LocalizationService _localization;
    private readonly ObservableCollection<NetworkInspectorRow> _rows = new();
    private readonly DataGrid _requests = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserResizeColumns = true,
        CanUserSortColumns = true,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        RowHeight = 28,
        ColumnHeaderHeight = 28,
        BorderThickness = new Avalonia.Thickness(0)
    };
    private readonly TextBox _general = CreateReadOnlyTextBox();
    private readonly TextBox _responseHeaders = CreateReadOnlyTextBox();
    private readonly TextBox _requestHeaders = CreateReadOnlyTextBox();
    private readonly TextBox _payload = CreateReadOnlyTextBox();
    private readonly TextBox _preview = CreateReadOnlyTextBox();
    private readonly TextBox _response = CreateReadOnlyTextBox();
    private readonly TextBlock _status = new() { Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _record = new() { MinWidth = 84 };
    private readonly Button _clear = new() { MinWidth = 64 };
    private readonly TextBox _filter = new() { Width = 300 };
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
        _requests.ItemsSource = _rows;
        _requests.SelectionChanged += (_, _) => ShowDetails((_requests.SelectedItem as NetworkInspectorRow)?.Id);

        AddColumn(T("network_inspector.name", "Name"), nameof(NetworkInspectorRow.Name), "*", 180);
        AddColumn(T("network_inspector.status", "Status"), nameof(NetworkInspectorRow.Status), "90", 76);
        AddColumn(T("network_inspector.type", "Type"), nameof(NetworkInspectorRow.Type), "160", 120);
        AddColumn(T("network_inspector.size", "Size"), nameof(NetworkInspectorRow.Size), "90", 72);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(12, 10, 12, 8)
        };
        toolbar.Children.Add(_record);
        toolbar.Children.Add(_clear);
        toolbar.Children.Add(_filter);
        toolbar.Children.Add(_status);

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("390,5,*") };
        content.Children.Add(_requests);
        content.Children.Add(new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns });
        content.Children.Add(CreateDetailsTabs());
        Grid.SetColumn(content.Children[1], 1);
        Grid.SetColumn(content.Children[2], 2);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar);
        root.Children.Add(content);
        Grid.SetRow(content, 1);
        Content = root;
    }

    private void AddColumn(string header, string property, string width, double minWidth)
    {
        _requests.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property),
            SortMemberPath = property,
            Width = new DataGridLength(width == "*" ? 1 : double.Parse(width), width == "*" ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel),
            MinWidth = minWidth
        });
    }

    private TabControl CreateDetailsTabs()
    {
        var tabs = new TabControl { Margin = new Avalonia.Thickness(0, 0, 12, 12) };
        tabs.Items.Add(new TabItem { Header = T("network_inspector.headers", "Headers"), Content = CreateHeadersPanel() });
        tabs.Items.Add(new TabItem { Header = T("network_inspector.payload", "Payload"), Content = _payload });
        tabs.Items.Add(new TabItem { Header = T("network_inspector.preview", "Preview"), Content = _preview });
        tabs.Items.Add(new TabItem { Header = T("network_inspector.response", "Response"), Content = _response });
        return tabs;
    }

    private Control CreateHeadersPanel()
    {
        var sections = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(8, 6, 8, 8) };
        sections.Children.Add(CreateSection(T("network_inspector.general", "General"), _general, 130));
        sections.Children.Add(CreateSection(T("network_inspector.response_headers", "Response Headers"), _responseHeaders, 180));
        sections.Children.Add(CreateSection(T("network_inspector.request_headers", "Request Headers"), _requestHeaders, 180));
        return new ScrollViewer { Content = sections };
    }

    private static Control CreateSection(string title, TextBox content, double minHeight)
    {
        content.MinHeight = minHeight;
        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                content
            }
        };
    }

    private void Refresh()
    {
        var selectedId = (_requests.SelectedItem as NetworkInspectorRow)?.Id;
        var snapshot = _diagnostics.GetSnapshot(new NetworkDiagnosticsQuery(Text: _filter.Text));
        _visible = snapshot.Entries;
        _rows.Clear();
        foreach (var entry in _visible)
            _rows.Add(CreateRow(entry));

        _record.Content = snapshot.State.IsRecording ? T("network_inspector.stop", "Stop") : T("network_inspector.record", "Record");
        _clear.Content = T("network_inspector.clear", "Clear");
        _filter.PlaceholderText = T("network_inspector.filter", "Filter (e.g. method:GET)");
        _status.Text = snapshot.State.IsAvailable
            ? $"{_visible.Count} / {snapshot.TotalEntryCount} {T("network_inspector.requests", "requests")}" : snapshot.State.UnavailableReason;

        _requests.SelectedItem = selectedId is long id ? _rows.FirstOrDefault(row => row.Id == id) : null;
        ShowDetails(selectedId);
    }

    private NetworkInspectorRow CreateRow(NetworkDiagnosticEntry entry) => new(
        entry.Id,
        entry.Name,
        entry.StatusCode?.ToString() ?? (entry.Outcome == NetworkDiagnosticOutcome.Succeeded ? "200" : "(failed)"),
        entry.ContentType ?? (entry.Kind == NetworkDiagnosticKind.SignalR ? "signalr" : "other"),
        FormatSize(entry.DeclaredContentLength ?? entry.ResponseBody?.ByteLength));

    private void ShowDetails(long? id)
    {
        var entry = id is long entryId ? _visible.FirstOrDefault(candidate => candidate.Id == entryId) : null;
        if (entry is null)
        {
            var empty = T("network_inspector.select", "Select a request to view its details.");
            _general.Text = empty;
            _responseHeaders.Text = empty;
            _requestHeaders.Text = empty;
            _payload.Text = empty;
            _preview.Text = empty;
            _response.Text = empty;
            return;
        }

        _general.Text = $"{T("network_inspector.request_url", "Request URL")}: {entry.RequestUrl ?? entry.PathAndQuery}\n"
            + $"{T("network_inspector.request_method", "Request Method")}: {entry.Method ?? "—"}\n"
            + $"{T("network_inspector.status", "Status")}: {entry.StatusCode?.ToString() ?? entry.Outcome.ToString()}\n"
            + $"{T("network_inspector.type", "Type")}: {entry.ContentType ?? "—"}\n"
            + $"{T("network_inspector.size", "Size")}: {FormatSize(entry.DeclaredContentLength ?? entry.ResponseBody?.ByteLength)}\n"
            + $"{T("network_inspector.duration", "Duration")}: {entry.Duration.TotalMilliseconds:0} ms\n"
            + $"{T("network_inspector.error", "Error")}: {entry.ErrorKind ?? "—"}";
        _responseHeaders.Text = FormatHeaders(entry.ResponseHeaders, "No response headers.");
        _requestHeaders.Text = FormatHeaders(entry.RequestHeaders, "No request headers.");
        _payload.Text = FormatPayload(entry.RequestBody, "No request payload.");
        _preview.Text = FormatPayload(entry.ResponseBody, "No response body.");
        _response.Text = FormatPayload(entry.ResponseBody, "No response body.");
    }

    private static TextBox CreateReadOnlyTextBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };

    private static string FormatHeaders(IReadOnlyDictionary<string, string>? headers, string emptyMessage) => headers is null || headers.Count == 0
        ? emptyMessage
        : string.Join(Environment.NewLine, headers.Select(header => $"{header.Key}: {header.Value}"));

    private static string FormatPayload(NetworkDiagnosticPayload? payload, string emptyMessage) => payload is null
        ? emptyMessage
        : $"{payload.Format}\n\n{payload.Content}";

    private static string FormatSize(long? bytes) => bytes is null ? "—"
        : bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} kB"
        : $"{bytes / 1024d / 1024d:0.0} MB";

    private void OnEntryCompleted(object? sender, NetworkDiagnosticEntry entry) => Dispatcher.UIThread.Post(Refresh);
    private void OnStateChanged(object? sender, NetworkDiagnosticsState state) => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(Refresh);
    private string T(string key, string fallback) => _localization.Get(key, fallback);

    private sealed record NetworkInspectorRow(long Id, string Name, string Status, string Type, string Size);
}
