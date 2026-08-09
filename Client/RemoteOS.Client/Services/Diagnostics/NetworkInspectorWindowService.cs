using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using Client.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        RowHeight = double.NaN,
        ColumnHeaderHeight = 28,
        BorderThickness = new Avalonia.Thickness(0)
    };
    private readonly TextBox _general = CreateReadOnlyTextBox();
    private readonly TextBox _responseHeaders = CreateReadOnlyTextBox();
    private readonly TextBox _requestHeaders = CreateReadOnlyTextBox();
    private readonly TextBox _payload = CreateReadOnlyTextBox();
    private readonly TextBox _response = CreateReadOnlyTextBox();
    private readonly TextBlock _status = new() { Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _record = new() { MinWidth = 84 };
    private readonly Button _clear = new() { MinWidth = 64 };
    private readonly TextBox _filter = new() { Width = 300 };
    private readonly Grid _content = new();
    private readonly GridSplitter _detailsSplitter = new() { Width = 5, ResizeDirection = GridResizeDirection.Columns };
    private Control? _details;
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

        AddColumn(T("network_inspector.name", "Name"), nameof(NetworkInspectorRow.Name), "220", 140);
        AddColumn(T("network_inspector.status", "Status"), nameof(NetworkInspectorRow.Status), "76", 60);
        AddColumn(T("network_inspector.type", "Type"), nameof(NetworkInspectorRow.Type), "120", 90);
        AddColumn(T("network_inspector.size", "Size"), nameof(NetworkInspectorRow.Size), "76", 60);

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

        _content.ColumnDefinitions = new ColumnDefinitions("*,0,0");
        _content.Children.Add(_requests);
        _content.Children.Add(_detailsSplitter);
        _details = CreateDetailsPanel();
        _content.Children.Add(_details);
        Grid.SetColumn(_detailsSplitter, 1);
        Grid.SetColumn(_details, 2);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar);
        root.Children.Add(_content);
        Grid.SetRow(_content, 1);
        Content = root;
    }

    private void AddColumn(string header, string property, string width, double minWidth)
    {
        _requests.Columns.Add(new DataGridTemplateColumn
        {
            Header = header,
            SortMemberPath = property,
            Width = new DataGridLength(width == "*" ? 1 : double.Parse(width), width == "*" ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel),
            MinWidth = minWidth,
            CellTemplate = new FuncDataTemplate<NetworkInspectorRow>((_, _) =>
            {
                var text = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                text.Bind(TextBlock.TextProperty, new Binding(property));
                return text;
            })
        });
    }

    private Control CreateDetailsPanel()
    {
        var close = new Button
        {
            Content = "×",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32
        };
        ToolTip.SetTip(close, T("network_inspector.close_details", "Close details"));
        close.Click += (_, _) => _requests.SelectedItem = null;

        var header = new Grid { Margin = new Avalonia.Thickness(0, 0, 12, 4) };
        header.Children.Add(close);

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        panel.Children.Add(header);
        panel.Children.Add(CreateDetailsTabs());
        Grid.SetRow(panel.Children[1], 1);
        return panel;
    }

    private TabControl CreateDetailsTabs()
    {
        var tabs = new TabControl { Margin = new Avalonia.Thickness(0, 0, 12, 12) };
        tabs.Items.Add(new TabItem { Header = T("network_inspector.headers", "Headers"), Content = CreateHeadersPanel() });
        tabs.Items.Add(new TabItem { Header = T("network_inspector.payload", "Payload"), Content = _payload });
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
            SetDetailsVisibility(false);
            var empty = T("network_inspector.select", "Select a request to view its details.");
            _general.Text = empty;
            _responseHeaders.Text = empty;
            _requestHeaders.Text = empty;
            _payload.Text = empty;
            _response.Text = empty;
            return;
        }

        SetDetailsVisibility(true);
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
        _response.Text = FormatResponse(entry);
    }

    private void SetDetailsVisibility(bool isVisible)
    {
        _detailsSplitter.IsVisible = isVisible;
        if (_details is not null)
            _details.IsVisible = isVisible;
        _content.ColumnDefinitions = new ColumnDefinitions(isVisible ? "390,5,*" : "*,0,0");
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

    private string FormatResponse(NetworkDiagnosticEntry entry)
    {
        var payload = entry.ResponseBody;
        if (payload is null)
            return T("network_inspector.response_empty", "No response body.");

        if (IsJson(entry.ContentType) || LooksLikeJson(payload.Content))
        {
            try
            {
                return JToken.Parse(payload.Content).ToString(Formatting.Indented);
            }
            catch (JsonReaderException)
            {
                return T("network_inspector.response_invalid_json", "The response contains invalid JSON and is not displayed.");
            }
        }

        var contentType = entry.ContentType ?? payload.Format;
        return string.Format(T("network_inspector.response_unsupported", "Response format: {0}. Content is not displayed."), contentType);
    }

    private static bool IsJson(string? contentType) => contentType?.Split(';', 2)[0].Trim() is { } mediaType
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeJson(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

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
