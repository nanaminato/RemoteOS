using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Localization;
using RemoteOS.AppSDK;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Docker.Views;

/// <summary>Focused editor dialogs. Each form section is deliberately independent for UI tuning.</summary>
internal static class DockerManagerDialogs
{
    public static Task ShowCreateContainerAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) => context.ShowDialogAsync<bool>(owner, LocalizedText.Get("docker.container.create"), dialog => CreateContainer(vm, dialog), new RemoteOS.Core.Primitives.Size(720, 690));
    public static Task ShowDeployStackAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) => context.ShowDialogAsync<bool>(owner, LocalizedText.Get("docker.stack.deploy"), dialog => CreateStack(vm, dialog), new RemoteOS.Core.Primitives.Size(760, 550));
    public static Task ShowPullImageAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) => context.ShowDialogAsync<bool>(owner, LocalizedText.Get("docker.image.pull"), dialog => CreatePullImage(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 230));
    public static Task ShowCreateNetworkAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) => context.ShowDialogAsync<bool>(owner, LocalizedText.Get("common.create"), dialog => CreateNetwork(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 280));
    public static Task ShowCreateVolumeAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) => context.ShowDialogAsync<bool>(owner, LocalizedText.Get("common.create"), dialog => CreateVolume(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 280));
    public static Task ShowDockerUnavailableAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) => context.ShowDialogAsync<bool>(owner, LocalizedText.Get("docker.unavailable_dialog.title"), dialog => Unavailable(vm, dialog), new RemoteOS.Core.Primitives.Size(460, 220));

    private static Control CreateContainer(DockerManagerViewModel vm, ModalDialog<bool> dialog)
    {
        var content = Panel(vm); content.Children.Add(new TextBlock { Text = LocalizedText.Get("docker.containers_hint"), TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#61708B") });
        content.Children.Add(Section("docker.container.section.identity", Row(TextField(vm, nameof(vm.ContainerName), "docker.container.name", "", 320), TextField(vm, nameof(vm.ContainerImage), "docker.container.image", "nginx:latest", 320))));
        content.Children.Add(Section("docker.container.section.runtime", Row(ChoiceField(vm, nameof(vm.ContainerNetwork), vm.AvailableNetworks, "docker.container.network", 320), ChoiceField(vm, nameof(vm.ContainerRestartPolicy), vm.RestartPolicies, "docker.container.restart", 320))));
        content.Children.Add(Section("docker.container.section.connectivity", Row(TextField(vm, nameof(vm.ContainerPorts), "docker.container.ports", "8080:80", 320), TextField(vm, nameof(vm.ContainerMounts), "docker.container.mounts", "volume:/data", 320))));
        content.Children.Add(Section("docker.container.section.configuration", Stack(TextField(vm, nameof(vm.ContainerEnvironment), "docker.container.environment", "KEY=value", 650, true, 66), TextField(vm, nameof(vm.ContainerArguments), "docker.container.arguments", "", 650, true, 66))));
        content.Children.Add(Actions(dialog, "docker.container.create", vm.TryCreateContainerAsync)); return new ScrollViewer { Content = content };
    }
    private static Control CreateStack(DockerManagerViewModel vm, ModalDialog<bool> dialog)
    {
        var content = Panel(vm); content.Children.Add(new TextBlock { Text = LocalizedText.Get("docker.stacks_hint"), TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#61708B"), Width = 700 }); content.Children.Add(TextField(vm, nameof(vm.StackName), "docker.stack.name", "my-stack", 350)); content.Children.Add(TextField(vm, nameof(vm.ComposeYaml), "docker.stack.compose", "services:\n  web:\n    image: nginx:latest", 700, true, 270)); content.Children.Add(StackActions(vm, dialog)); return new ScrollViewer { Content = content };
    }
    private static Control CreatePullImage(DockerManagerViewModel vm, ModalDialog<bool> dialog) { var content = Panel(vm); content.Children.Add(TextField(vm, nameof(vm.ImageReference), "docker.image.reference", "nginx:latest", 420)); content.Children.Add(Actions(dialog, "docker.image.pull", vm.TryPullImageAsync)); return content; }
    private static Control CreateNetwork(DockerManagerViewModel vm, ModalDialog<bool> dialog) { var content = Panel(vm); content.Children.Add(TextField(vm, nameof(vm.NetworkName), "common.name", "", 420)); content.Children.Add(ChoiceField(vm, nameof(vm.SelectedNetworkDriver), vm.NetworkDrivers, "docker.network.driver", 220)); content.Children.Add(Actions(dialog, "common.create", vm.TryCreateNetworkAsync)); return content; }
    private static Control CreateVolume(DockerManagerViewModel vm, ModalDialog<bool> dialog) { var content = Panel(vm); content.Children.Add(TextField(vm, nameof(vm.VolumeName), "common.name", "", 420)); content.Children.Add(ChoiceField(vm, nameof(vm.SelectedVolumeDriver), vm.VolumeDrivers, "docker.volume.driver", 220)); content.Children.Add(Actions(dialog, "common.create", vm.TryCreateVolumeAsync)); return content; }
    private static Control Unavailable(DockerManagerViewModel vm, ModalDialog<bool> dialog)
    {
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 16 }; content.Children.Add(new TextBlock { Text = LocalizedText.Get("docker.unavailable_dialog.message"), TextWrapping = TextWrapping.Wrap }); var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 }; var refresh = new Button { Content = LocalizedText.Get("common.refresh") }; refresh.Click += (_, _) => { dialog.Close(true); vm.RefreshCommand.Execute(null); }; var confirm = new Button { Content = LocalizedText.Get("common.ok"), Classes = { "primary" } }; confirm.Click += (_, _) => dialog.Close(true); actions.Children.Add(refresh); actions.Children.Add(confirm); content.Children.Add(actions); return content;
    }
    private static StackPanel Panel(DockerManagerViewModel vm) => new() { Spacing = 12, Margin = new Thickness(20), DataContext = vm, HorizontalAlignment = HorizontalAlignment.Left };
    private static Control Section(string titleKey, Control content) => new StackPanel { Spacing = 7, Children = { new TextBlock { Text = LocalizedText.Get(titleKey), FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#36506F") }, content } };
    private static Control Row(params Control[] children) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; foreach (var child in children) row.Children.Add(child); return row; }
    private static Control Stack(params Control[] children) { var stack = new StackPanel { Spacing = 8 }; foreach (var child in children) stack.Children.Add(child); return stack; }
    private static Control Actions(ModalDialog<bool> dialog, string confirmKey, Func<Task<bool>> submit) { var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) }; var cancel = new Button { Content = LocalizedText.Get("common.cancel") }; cancel.Click += (_, _) => dialog.Cancel(); var confirm = new Button { Content = LocalizedText.Get(confirmKey), Classes = { "primary" } }; confirm.Click += async (_, _) => { if (await submit()) dialog.Close(true); }; actions.Children.Add(cancel); actions.Children.Add(confirm); return actions; }
    private static Control StackActions(DockerManagerViewModel vm, ModalDialog<bool> dialog) { var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) }; actions.Children.Add(new Button { Content = LocalizedText.Get("docker.stack.validate"), Command = vm.ValidateStackCommand }); var deploy = new Button { Content = LocalizedText.Get("docker.stack.deploy"), Classes = { "primary" } }; deploy.Click += async (_, _) => { if (await vm.TryDeployStackAsync()) dialog.Close(true); }; actions.Children.Add(deploy); var cancel = new Button { Content = LocalizedText.Get("common.cancel") }; cancel.Click += (_, _) => dialog.Cancel(); actions.Children.Add(cancel); return actions; }
    private static Control TextField(DockerManagerViewModel vm, string property, string labelKey, string placeholder, double width, bool multiline = false, double minHeight = 0) { var field = new StackPanel { Spacing = 4, Width = width, HorizontalAlignment = HorizontalAlignment.Left }; field.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey), Foreground = Brush.Parse("#36506F") }); var box = new TextBox { PlaceholderText = placeholder, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = minHeight }; box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay }); field.Children.Add(box); return field; }
    private static Control ChoiceField(DockerManagerViewModel vm, string property, IEnumerable<string> options, string labelKey, double width) { var field = new StackPanel { Spacing = 4, Width = width, HorizontalAlignment = HorizontalAlignment.Left }; field.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey), Foreground = Brush.Parse("#36506F") }); var box = new ComboBox { ItemsSource = options }; box.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(property) { Mode = BindingMode.TwoWay }); field.Children.Add(box); return field; }
}
