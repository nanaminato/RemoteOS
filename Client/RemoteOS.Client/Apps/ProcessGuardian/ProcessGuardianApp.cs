using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.ProcessGuardian;
using Rect = RemoteOS.Core.Primitives.Rect;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.ProcessGuardian;

/// <summary>Built-in UI for workloads supervised by the separately installed Guardian Agent.</summary>
public sealed class ProcessGuardianApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.processguardian"), "Process Guardian", "0.1.0", "🛡", "View RemoteOS Guardian Agent workloads", [AppPermissions.ServerGuardianRead, AppPermissions.ServerGuardianManage]);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IProcessGuardianClient)) as IProcessGuardianClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.processguardian.display_name"),
                new TextBlock { Text = LocalizedText.Get("guardian.login_required"), Margin = new Avalonia.Thickness(24), TextWrapping = TextWrapping.Wrap },
                new Rect(200, 160, 460, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var viewModel = new ProcessGuardianViewModel(client);
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.processguardian.display_name"), CreateView(viewModel), new Rect(80, 60, 1240, 700), Manifest.IconGlyph);
        viewModel.ShowEditorAsync = async isEdit =>
        {
            await context.ShowDialogAsync<bool>(window,
                LocalizedText.Get(isEdit ? "guardian.editor.edit_title" : "guardian.editor.create_title"), dialog =>
                {
                    viewModel.CloseEditorAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return CreateEditorView(viewModel, dialog);
                }, new RemoteOS.Core.Primitives.Size(560, 520));
        };
        viewModel.ShowLogsAsync = workload =>
        {
            var logViewModel = new GuardianLogWindowViewModel(session, workload);
            var logWindow = context.ShowWindow(logViewModel.Title, CreateLogsView(logViewModel), new Rect(170, 120, 800, 520), Manifest.IconGlyph);
            logWindow.CloseRequested += (_, _) => _ = logViewModel.DisposeAsync();
            _ = logViewModel.StartAsync();
            return Task.CompletedTask;
        };
        _ = viewModel.StartAsync();
    }

    private static Control CreateView(ProcessGuardianViewModel vm)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(18), LastChildFill = true, DataContext = vm };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("guardian.create.open"), Command = vm.OpenCreateWorkloadCommand, Classes = { "primary" } });
        var refresh = new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand, MinWidth = 38 };
        Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);

        var notice = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F3F4F6")),
            Padding = new Avalonia.Thickness(14, 10),
            CornerRadius = new CornerRadius(3),
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = LocalizedText.Get("guardian.info"), TextWrapping = TextWrapping.Wrap }
        };
        DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);

        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#64748B")) };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        root.Children.Add(CreateWorkloadTable(vm));
        return root;
    }

    private static DataGrid CreateWorkloadTable(ProcessGuardianViewModel vm)
    {
        var table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            DataContext = vm
        };
        // Avalonia DataGrid owns its item source property; binding the base ItemsControl
        // property leaves the grid visually empty even though Workloads is populated.
        table.ItemsSource = vm.Workloads;
        table.Columns.Add(TextColumn("guardian.table.name", nameof(GuardianWorkloadDto.Name), "140"));
        table.Columns.Add(TextColumn("guardian.table.command", nameof(GuardianWorkloadDto.ExecutablePath), "220"));
        table.Columns.Add(TextColumn("guardian.table.working_directory", nameof(GuardianWorkloadDto.WorkingDirectory), "180"));
        table.Columns.Add(TextColumn("guardian.table.pid", nameof(GuardianWorkloadDto.ProcessId), "80"));
        table.Columns.Add(TextColumn("guardian.table.restarts", nameof(GuardianWorkloadDto.RestartCount), "80"));
        table.Columns.Add(TextColumn("guardian.table.state", nameof(GuardianWorkloadDto.ActualState), "100"));
        table.Columns.Add(new DataGridTemplateColumn
        {
            Header = LocalizedText.Get("guardian.table.actions"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            CellTemplate = new FuncDataTemplate<GuardianWorkloadDto>((_, _) => CreateActionButtons(vm))
        });
        return table;
    }

    private static DataGridTextColumn TextColumn(string headerKey, string property, string width) => new()
    {
        Header = LocalizedText.Get(headerKey),
        Binding = new Avalonia.Data.Binding(property),
        Width = new DataGridLength(double.Parse(width, System.Globalization.CultureInfo.InvariantCulture), DataGridLengthUnitType.Pixel)
    };

    private static Control CreateActionButtons(ProcessGuardianViewModel vm)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(4, 2) };
        actions.Children.Add(ActionButton("guardian.action.edit", vm.EditWorkloadCommand));
        actions.Children.Add(ActionButton("guardian.logs.show", vm.OpenLogsCommand));
        actions.Children.Add(ActionButton("guardian.action.start", vm.StartWorkloadCommand));
        actions.Children.Add(ActionButton("guardian.action.stop", vm.StopWorkloadCommand));
        actions.Children.Add(ActionButton("guardian.action.restart", vm.RestartWorkloadCommand));
        actions.Children.Add(ActionButton("guardian.action.delete", vm.DeleteWorkloadCommand));
        return actions;
    }

    private static Button ActionButton(string textKey, System.Windows.Input.ICommand command)
    {
        var button = new Button { Content = LocalizedText.Get(textKey), Command = command, Padding = new Avalonia.Thickness(4, 2) };
        button.Bind(Button.CommandParameterProperty, new Avalonia.Data.Binding());
        return button;
    }

    private static Control CreateEditorView(ProcessGuardianViewModel vm, RemoteOS.WindowManager.ModalDialog<bool> dialog)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(20), DataContext = vm };
        panel.Children.Add(EditorField("guardian.create.id", nameof(vm.DefinitionId)));
        panel.Children.Add(EditorField("guardian.create.name", nameof(vm.DefinitionName)));
        panel.Children.Add(EditorField("guardian.create.executable", nameof(vm.ExecutablePath)));
        panel.Children.Add(EditorField("guardian.create.working_directory", nameof(vm.WorkingDirectory)));
        panel.Children.Add(EditorField("guardian.create.arguments", nameof(vm.ArgumentsText), true));
        var enabledOnBoot = new CheckBox { Content = LocalizedText.Get("guardian.create.enabled_on_boot") };
        enabledOnBoot.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.EnabledOnBoot)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        panel.Children.Add(enabledOnBoot);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
        cancel.Click += (_, _) => dialog.Cancel();
        actions.Children.Add(cancel);
        actions.Children.Add(new Button { Content = LocalizedText.Get("guardian.create.submit"), Command = vm.CreateWorkloadCommand, Classes = { "primary" } });
        panel.Children.Add(actions);
        return new ScrollViewer { Content = panel };
    }

    private static Control CreateLogsView(GuardianLogWindowViewModel vm)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(16), DataContext = vm };
        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 8), Foreground = new SolidColorBrush(Color.Parse("#64748B")) };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var logs = new ListBox();
        logs.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(vm.Lines)));
        root.Children.Add(logs);
        return root;
    }

    private static TextBox EditorField(string labelKey, string property, bool acceptsReturn = false) => new()
    {
        PlaceholderText = LocalizedText.Get(labelKey),
        AcceptsReturn = acceptsReturn,
        MinHeight = acceptsReturn ? 96 : 0,
        TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
        [!TextBox.TextProperty] = new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay }
    };
}
