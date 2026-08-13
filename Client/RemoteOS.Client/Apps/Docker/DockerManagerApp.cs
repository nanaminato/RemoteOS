using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using Rect = RemoteOS.Core.Primitives.Rect;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Docker;

/// <summary>Built-in client for the server-local Docker Manager.</summary>
public sealed class DockerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.docker"), "Docker Manager", "0.1.0", "🐳", "Manage the local Docker Engine on the RemoteOS Server", [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteDockerClient)) as IRemoteDockerClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.docker.display_name"), LoginRequired(), new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new DockerManagerViewModel(client);
        context.ShowWindow(LocalizedText.Get("application.remoteos.docker.display_name"), CreateView(viewModel), new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        _ = viewModel.StartAsync();
    }

    private static Control LoginRequired() => new TextBlock { Text = LocalizedText.Get("docker.login_required"), Margin = new Avalonia.Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private static Control CreateView(DockerManagerViewModel vm)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(18), LastChildFill = true };
        var refresh = new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(refresh, Dock.Top); root.Children.Add(refresh);
        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 14), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText))); DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var tabs = new TabControl { DataContext = vm };
        tabs.Items.Add(ContainerTab(vm));
        tabs.Items.Add(StackTab(vm));
        tabs.Items.Add(ImageTab(vm));
        tabs.Items.Add(Tab("docker.networks", nameof(vm.Networks), "Name"));
        tabs.Items.Add(Tab("docker.volumes", nameof(vm.Volumes), "Name"));
        root.Children.Add(tabs); return root;
    }
    private static TabItem Tab(string titleKey, string source, string displayMember) => new()
    {
        Header = LocalizedText.Get(titleKey),
        Content = new ListBox { [!ItemsControl.ItemsSourceProperty] = new Avalonia.Data.Binding(source), DisplayMemberBinding = new Avalonia.Data.Binding(displayMember), Margin = new Avalonia.Thickness(0, 8, 0, 0) }
    };
    private static TabItem ContainerTab(DockerManagerViewModel vm)
    {
        var panel = new DockPanel();
        var create = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        create.Children.Add(new TextBox { PlaceholderText = LocalizedText.Get("docker.container.name"), [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.ContainerName)) { Mode = Avalonia.Data.BindingMode.TwoWay } });
        create.Children.Add(new TextBox { PlaceholderText = LocalizedText.Get("docker.container.image"), [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.ContainerImage)) { Mode = Avalonia.Data.BindingMode.TwoWay } });
        create.Children.Add(new TextBox { PlaceholderText = LocalizedText.Get("docker.container.arguments"), AcceptsReturn = true, MinHeight = 36, [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.ContainerArguments)) { Mode = Avalonia.Data.BindingMode.TwoWay } });
        create.Children.Add(new Button { Content = LocalizedText.Get("docker.container.create"), Command = vm.CreateContainerCommand, HorizontalAlignment = HorizontalAlignment.Left });
        DockPanel.SetDock(create, Dock.Top); panel.Children.Add(create);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.action.start"), Command = vm.StartContainerCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.action.stop"), Command = vm.StopContainerCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.action.restart"), Command = vm.RestartContainerCommand });
        DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions);
        panel.Children.Add(new ListBox { [!ItemsControl.ItemsSourceProperty] = new Avalonia.Data.Binding(nameof(vm.Containers)), [!ListBox.SelectedItemProperty] = new Avalonia.Data.Binding(nameof(vm.SelectedContainer)) { Mode = Avalonia.Data.BindingMode.TwoWay }, DisplayMemberBinding = new Avalonia.Data.Binding("Names") });
        return new TabItem { Header = LocalizedText.Get("docker.containers"), Content = panel };
    }
    private static TabItem StackTab(DockerManagerViewModel vm)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBox { PlaceholderText = LocalizedText.Get("docker.stack.name"), [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.StackName)) { Mode = Avalonia.Data.BindingMode.TwoWay } });
        panel.Children.Add(new TextBox { PlaceholderText = LocalizedText.Get("docker.stack.compose"), AcceptsReturn = true, MinHeight = 300, TextWrapping = Avalonia.Media.TextWrapping.NoWrap, [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.ComposeYaml)) { Mode = Avalonia.Data.BindingMode.TwoWay } });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(new Button { Content = LocalizedText.Get("docker.stack.validate"), Command = vm.ValidateStackCommand });
        buttons.Children.Add(new Button { Content = LocalizedText.Get("docker.stack.deploy"), Command = vm.DeployStackCommand });
        buttons.Children.Add(new Button { Content = LocalizedText.Get("docker.stack.down"), Command = vm.DownStackCommand });
        panel.Children.Add(buttons);
        return new TabItem { Header = LocalizedText.Get("docker.stacks"), Content = panel };
    }
    private static TabItem ImageTab(DockerManagerViewModel vm)
    {
        var panel = new DockPanel();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(new TextBox { Width = 300, PlaceholderText = LocalizedText.Get("docker.image.reference"), [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.ImageReference)) { Mode = Avalonia.Data.BindingMode.TwoWay } });
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("docker.image.pull"), Command = vm.PullImageCommand });
        toolbar.Children.Add(new CheckBox { Content = LocalizedText.Get("docker.image.delete_confirm"), [!ToggleButton.IsCheckedProperty] = new Avalonia.Data.Binding(nameof(vm.ConfirmImageDeletion)) { Mode = Avalonia.Data.BindingMode.TwoWay } });
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("docker.image.delete"), Command = vm.DeleteImageCommand });
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        panel.Children.Add(new ListBox { [!ItemsControl.ItemsSourceProperty] = new Avalonia.Data.Binding(nameof(vm.Images)), [!ListBox.SelectedItemProperty] = new Avalonia.Data.Binding(nameof(vm.SelectedImage)) { Mode = Avalonia.Data.BindingMode.TwoWay }, DisplayMemberBinding = new Avalonia.Data.Binding("Repository") });
        return new TabItem { Header = LocalizedText.Get("docker.images"), Content = panel };
    }
}
