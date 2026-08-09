using Avalonia.Controls;
using Avalonia.Media;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;

namespace RemoteOS.Examples.NetworkInspector;

/// <summary>A development package UI for the host-provided, redacted network diagnostics capability.</summary>
public sealed class NetworkInspectorApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(
        new AppId(NetworkDiagnosticsApplication.InspectorAppId), "Network Inspector", "0.1.0-dev", "🔎",
        "Redacted RemoteOS REST and SignalR diagnostics", ["diagnostics.network.read"]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        var viewModel = new NetworkInspectorViewModel(context.NetworkDiagnostics, context.SystemLanguage);
        var view = new NetworkInspectorView { DataContext = viewModel };
        var window = context.Windows.ShowWindow(viewModel.Title, view, new Rect(110, 70, 1180, 720), "🔎");
        window.Closed.Register(viewModel.Dispose);
        return Task.CompletedTask;
    }
}

internal sealed class NetworkInspectorView : UserControl
{
    public NetworkInspectorView()
    {
        var record = new Button { MinWidth = 100, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        record.Bind(Button.ContentProperty, new Avalonia.Data.Binding("RecordButtonText"));
        record.Bind(Button.CommandProperty, new Avalonia.Data.Binding("ToggleRecordingCommand"));
        var clear = new Button { MinWidth = 80, Margin = new Avalonia.Thickness(0, 0, 12, 0) };
        clear.Bind(Button.ContentProperty, new Avalonia.Data.Binding("ClearText"));
        clear.Bind(Button.CommandProperty, new Avalonia.Data.Binding("ClearCommand"));
        var filter = new TextBox { Width = 280 };
        filter.Bind(TextBox.PlaceholderTextProperty, new Avalonia.Data.Binding("FilterPlaceholder"));
        filter.Bind(TextBox.TextProperty, new Avalonia.Data.Binding("Filter") { Mode = Avalonia.Data.BindingMode.TwoWay });

        var toolbar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new Avalonia.Thickness(16, 14, 16, 10) };
        toolbar.Children.Add(record);
        toolbar.Children.Add(clear);
        toolbar.Children.Add(filter);
        var status = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(12, 0, 0, 0), Opacity = 0.72 };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Status"));
        toolbar.Children.Add(status);

        var list = new ListBox { Margin = new Avalonia.Thickness(16, 0, 16, 8) };
        list.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding("VisibleEntries"));
        list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
            new Avalonia.Data.Binding("SelectedEntry") { Mode = Avalonia.Data.BindingMode.TwoWay });
        var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 170,
            Margin = new Avalonia.Thickness(16, 0, 16, 16), FontFamily = FontFamily.Default };
        details.Bind(TextBox.TextProperty, new Avalonia.Data.Binding("Details"));

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children = { toolbar, list, details },
        };
        Grid.SetRow(list, 1);
        Grid.SetRow(details, 2);
    }
}
