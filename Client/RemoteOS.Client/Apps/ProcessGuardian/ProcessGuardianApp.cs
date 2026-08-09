using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.ProcessGuardian;
using Rect = RemoteOS.Core.Primitives.Rect;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.ProcessGuardian;

/// <summary>Built-in guardian console. It exposes Agent availability without substituting Server supervision.</summary>
public sealed class ProcessGuardianApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.processguardian"), "Process Guardian", "0.1.0", "🛡", "View RemoteOS Guardian Agent workloads", [AppPermissions.ServerGuardianRead, AppPermissions.ServerGuardianManage]);
    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IProcessGuardianClient)) as IProcessGuardianClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated) { context.ShowWindow(LocalizedText.Get("application.remoteos.processguardian.display_name"), new TextBlock { Text = LocalizedText.Get("guardian.login_required"), Margin = new Avalonia.Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Rect(200, 160, 460, 180), Manifest.IconGlyph, false, false, false); return; }
        var viewModel = new ProcessGuardianViewModel(client);
        context.ShowWindow(LocalizedText.Get("application.remoteos.processguardian.display_name"), CreateView(viewModel), new Rect(120, 90, 760, 480), Manifest.IconGlyph);
        _ = viewModel.StartAsync();
    }
    private static Control CreateView(ProcessGuardianViewModel viewModel)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(18) };
        var create = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 0, 0, 12) };
        create.Children.Add(BoundTextBox("guardian.create.id", nameof(viewModel.DefinitionId)));
        create.Children.Add(BoundTextBox("guardian.create.name", nameof(viewModel.DefinitionName)));
        create.Children.Add(BoundTextBox("guardian.create.executable", nameof(viewModel.ExecutablePath)));
        create.Children.Add(BoundTextBox("guardian.create.working_directory", nameof(viewModel.WorkingDirectory)));
        create.Children.Add(BoundTextBox("guardian.create.arguments", nameof(viewModel.ArgumentsText), true));
        var enabledOnBoot = new CheckBox { Content = LocalizedText.Get("guardian.create.enabled_on_boot") };
        enabledOnBoot.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(viewModel.EnabledOnBoot)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        create.Children.Add(enabledOnBoot);
        create.Children.Add(new Button { Content = LocalizedText.Get("guardian.create.submit"), Command = viewModel.CreateWorkloadCommand, HorizontalAlignment = HorizontalAlignment.Left });
        DockPanel.SetDock(create, Dock.Top); root.Children.Add(create);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("common.refresh"), Command = viewModel.RefreshCommand });
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("guardian.action.start"), Command = viewModel.StartWorkloadCommand });
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("guardian.action.stop"), Command = viewModel.StopWorkloadCommand });
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("guardian.action.restart"), Command = viewModel.RestartWorkloadCommand });
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("guardian.action.delete"), Command = viewModel.DeleteWorkloadCommand });
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("guardian.logs.show"), Command = viewModel.LoadLogsCommand });
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 12, 0, 12), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(viewModel.StatusText))); DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        content.Children.Add(new ListBox { [!ItemsControl.ItemsSourceProperty] = new Avalonia.Data.Binding(nameof(viewModel.Workloads)), [!ListBox.SelectedItemProperty] = new Avalonia.Data.Binding(nameof(viewModel.SelectedWorkload)) { Mode = Avalonia.Data.BindingMode.TwoWay }, DisplayMemberBinding = new Avalonia.Data.Binding("Name") });
        var logs = new ListBox { [!ItemsControl.ItemsSourceProperty] = new Avalonia.Data.Binding(nameof(viewModel.Logs)), DisplayMemberBinding = new Avalonia.Data.Binding("Message") }; Grid.SetColumn(logs, 1); content.Children.Add(logs);
        root.Children.Add(content);
        return root;
    }
    private static TextBox BoundTextBox(string labelKey, string property, bool acceptsReturn = false) => new()
    {
        PlaceholderText = LocalizedText.Get(labelKey), AcceptsReturn = acceptsReturn, MinHeight = acceptsReturn ? 48 : 0,
        [!TextBox.TextProperty] = new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay }
    };
}
