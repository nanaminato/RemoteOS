using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.WebServers;
using Rect = RemoteOS.Core.Primitives.Rect;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.WebServers;

/// <summary>Built-in web server manager. Host-global Nginx discovery, config test, integrate, reload.</summary>
public sealed class WebServerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.webservers"), "Web Server Manager", "0.1.0", "🌐", "Manage web servers on the RemoteOS Server",
        [AppPermissions.ServerWebServersRead, AppPermissions.ServerWebServersManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteWebServerClient)) as IRemoteWebServerClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.webservers.display_name"),
                new TextBlock { Text = LocalizedText.Get("webservers.login_required"), Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap },
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new WebServerManagerViewModel(client, session, context.Permissions);
        context.ShowWindow(LocalizedText.Get("application.remoteos.webservers.display_name"),
            CreateView(viewModel), new Rect(70, 55, 1080, 680), Manifest.IconGlyph);
        _ = viewModel.StartAsync();
    }

    private static Control CreateView(WebServerManagerViewModel vm)
    {
        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true, DataContext = vm };

        // Toolbar: refresh + discover + cancel + operation status.
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(toolbar, Dock.Top);
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand });
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("webservers.discover"), Command = vm.DiscoverCommand });
        var cancel = new Button { Content = LocalizedText.Get("common.stop"), Command = vm.CancelOperationCommand };
        cancel.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(vm.IsOperationRunning)));
        toolbar.Children.Add(cancel);
        var operation = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) };
        operation.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.OperationText)));
        toolbar.Children.Add(operation);
        root.Children.Add(toolbar);

        // Status line.
        var status = new TextBlock { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
        DockPanel.SetDock(status, Dock.Top);
        root.Children.Add(status);

        // Selected-server detail + actions.
        var detail = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(detail, Dock.Top);
        var selectedStatus = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        selectedStatus.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.SelectedStatusText)));
        detail.Children.Add(selectedStatus);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(new Button { Content = LocalizedText.Get("webservers.action.test_config"), Command = vm.TestConfigurationCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("webservers.action.refresh_status"), Command = vm.RefreshStatusCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("webservers.action.integrate"), Command = vm.IntegrateCommand, Classes = { "primary" } });
        actions.Children.Add(new Button { Content = LocalizedText.Get("webservers.action.reload"), Command = vm.ReloadCommand });
        detail.Children.Add(actions);
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
        testResult.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.TestResultText)));
        detail.Children.Add(testResult);
        root.Children.Add(detail);

        root.Children.Add(CreateServerTable(vm));
        return root;
    }

    private static DataGrid CreateServerTable(WebServerManagerViewModel vm)
    {
        var table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ItemsSource = vm.Servers
        };
        table.Columns.Add(Column("webservers.column.id", nameof(WebServerDto.Id), 140));
        table.Columns.Add(Column("webservers.column.type", nameof(WebServerDto.Type), 110));
        table.Columns.Add(Column("webservers.column.management_mode", nameof(WebServerDto.ManagementMode), 140));
        table.Columns.Add(Column("webservers.column.executable", nameof(WebServerDto.ExecutablePath), 280));
        table.Columns.Add(Column("webservers.column.config", nameof(WebServerDto.ConfigurationPath), 260));
        table.Columns.Add(Column("webservers.column.version", nameof(WebServerDto.Version), 120));
        table.SelectionChanged += (_, _) => vm.SelectedServer = table.SelectedItem as WebServerDto;
        return table;
    }

    private static DataGridTextColumn Column(string headerKey, string property, double width) => new()
    {
        Header = LocalizedText.Get(headerKey), Binding = new Avalonia.Data.Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel)
    };
}
