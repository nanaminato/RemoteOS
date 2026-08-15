using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.Docker;
using RemoteOS.WindowManager;
using Rect = RemoteOS.Core.Primitives.Rect;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Docker;

/// <summary>Built-in client for the server-local Docker Engine.</summary>
public sealed class DockerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.docker"), "Docker Manager", "0.2.0", "🐳", "Manage the local Docker Engine on the RemoteOS Server", [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

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
        ManagedWindow? window = null;
        var view = CreateView(viewModel,
            () => window is null ? Task.CompletedTask : ShowCreateContainerDialogAsync(context, window, viewModel),
            () => window is null ? Task.CompletedTask : ShowPullImageDialogAsync(context, window, viewModel),
            () => window is null ? Task.CompletedTask : ShowCreateNetworkDialogAsync(context, window, viewModel),
            () => window is null ? Task.CompletedTask : ShowCreateVolumeDialogAsync(context, window, viewModel));
        window = context.ShowWindow(LocalizedText.Get("application.remoteos.docker.display_name"), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        _ = viewModel.StartAsync();
    }

    private static Control LoginRequired() => new TextBlock { Text = LocalizedText.Get("docker.login_required"), Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };

    private static Control CreateView(
        DockerManagerViewModel vm,
        Func<Task> showCreateContainer,
        Func<Task> showPullImage,
        Func<Task> showCreateNetwork,
        Func<Task> showCreateVolume)
    {
        var root = new Grid { DataContext = vm, Background = Brush.Parse("#F4F7FB"), RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(CreateHeader(vm));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
        Grid.SetRow(body, 1); root.Children.Add(body);
        var navigation = new StackPanel { Spacing = 4, Margin = new Thickness(14, 22) };
        body.Children.Add(new Border { Background = Brush.Parse("#EEF3FA"), BorderBrush = Brush.Parse("#DCE5F1"), BorderThickness = new Thickness(0, 0, 1, 0), Child = navigation });
        var content = new ContentControl { Margin = new Thickness(30, 24) };
        var contentScroller = new ScrollViewer { Content = content };
        Grid.SetColumn(contentScroller, 1);
        body.Children.Add(contentScroller);

        var selectedButton = default(Button);
        void Show(string section, Button button)
        {
            if (selectedButton is not null) { selectedButton.Background = Brushes.Transparent; selectedButton.Foreground = Brush.Parse("#36506F"); }
            selectedButton = button; button.Background = Brush.Parse("#DCEBFF"); button.Foreground = Brush.Parse("#1769D9");
            content.Content = section switch
            {
                "overview" => Overview(vm),
                "containers" => Containers(vm, showCreateContainer),
                "stacks" => Stacks(vm),
                "images" => Images(vm, showPullImage),
                "networks" => Networks(vm, showCreateNetwork),
                "volumes" => Volumes(vm, showCreateVolume),
                _ => Overview(vm)
            };
        }
        navigation.Children.Add(new TextBlock { Text = LocalizedText.Get("docker.workspace"), Foreground = Brush.Parse("#72819A"), FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 0, 0, 6) });
        foreach (var (id, title) in new[]
        {
            ("overview", LocalizedText.Get("docker.overview")), ("containers", LocalizedText.Get("docker.containers")), ("stacks", LocalizedText.Get("docker.stacks")),
            ("images", LocalizedText.Get("docker.images")), ("networks", LocalizedText.Get("docker.networks")), ("volumes", LocalizedText.Get("docker.volumes"))
        })
        {
            var button = new Button { Content = title, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8), Background = Brushes.Transparent, Foreground = Brush.Parse("#36506F") };
            button.Click += (_, _) => Show(id, button);
            navigation.Children.Add(button);
            if (id == "overview") Show(id, button);
        }
        return root;
    }

    private static Control CreateHeader(DockerManagerViewModel vm)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(new Border { Width = 46, Height = 46, Background = Brush.Parse("#147CB8"), CornerRadius = new CornerRadius(14), Child = new TextBlock { Text = "🐳", FontSize = 25, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var title = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = LocalizedText.Get("application.remoteos.docker.display_name"), FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
        title.Children.Add(new TextBlock { Text = LocalizedText.Get("docker.subtitle"), FontSize = 13, Foreground = Brush.Parse("#B8C9E6") });
        Grid.SetColumn(title, 1); header.Children.Add(title);
        var status = new TextBlock { FontSize = 12, Foreground = Brush.Parse("#C8F6DD") };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(vm.StatusText)));
        var pill = new Border { Background = Brush.Parse("#1C3765"), CornerRadius = new CornerRadius(16), Padding = new Thickness(11, 5), VerticalAlignment = VerticalAlignment.Center, Child = status };
        Grid.SetColumn(pill, 2); header.Children.Add(pill);
        return new Border { Background = Brush.Parse("#122344"), Padding = new Thickness(28, 18), Child = header };
    }

    private static Control Overview(DockerManagerViewModel vm)
    {
        var layout = new StackPanel { Spacing = 18 };
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 18 };
        cards.Children.Add(MetricCard(LocalizedText.Get("docker.engine"), nameof(vm.EngineVersion), nameof(vm.EnginePlatform)));
        var running = MetricCard(LocalizedText.Get("docker.running_containers"), nameof(vm.RunningContainerCount), LocalizedText.Get("docker.running_hint"));
        Grid.SetColumn(running, 1); cards.Children.Add(running); layout.Children.Add(cards);
        var summary = Card(LocalizedText.Get("docker.overview"), LocalizedText.Get("docker.overview_hint"));
        var contents = (StackPanel)summary.Child!;
        var counts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 10 };
        AddSummaryCount(counts, LocalizedText.Get("docker.containers"), nameof(vm.Containers), 0);
        AddSummaryCount(counts, LocalizedText.Get("docker.images"), nameof(vm.Images), 1);
        AddSummaryCount(counts, LocalizedText.Get("docker.networks"), nameof(vm.Networks), 2);
        AddSummaryCount(counts, LocalizedText.Get("docker.volumes"), nameof(vm.Volumes), 3);
        contents.Children.Add(counts);
        var refresh = new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        contents.Children.Add(refresh); layout.Children.Add(summary);
        layout.Children.Add(new Border { Background = Brush.Parse("#EDF4FF"), BorderBrush = Brush.Parse("#CFE1FF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Child = new TextBlock { Text = LocalizedText.Get("docker.safety_hint"), Foreground = Brush.Parse("#315582"), TextWrapping = TextWrapping.Wrap } });
        return layout;
    }

    private static Control Containers(DockerManagerViewModel vm, Func<Task> showCreateDialog)
    {
        var card = Card(LocalizedText.Get("docker.containers"), LocalizedText.Get("docker.containers_hint"));
        var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("docker.container.create", showCreateDialog));
        content.Children.Add(Separator());
        content.Children.Add(ContainerTable(vm));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), ItemHeight = 34 };
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.action.start"), Command = vm.StartContainerCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.action.stop"), Command = vm.StopContainerCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.action.restart"), Command = vm.RestartContainerCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.action.pause"), Command = vm.PauseContainerCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.action.unpause"), Command = vm.UnpauseContainerCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.container.logs"), Command = vm.LoadContainerLogsCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.container.stats"), Command = vm.LoadContainerStatsCommand });
        actions.Children.Add(new CheckBox { Content = LocalizedText.Get("docker.container.delete_confirm"), VerticalAlignment = VerticalAlignment.Center });
        ((CheckBox)actions.Children[^1]).Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(vm.ConfirmContainerDeletion)) { Mode = BindingMode.TwoWay });
        actions.Children.Add(new Button { Content = LocalizedText.Get("common.delete"), Command = vm.DeleteContainerCommand, Foreground = Brush.Parse("#B42318") });
        content.Children.Add(actions);
        content.Children.Add(ReadOnlyBox(vm, nameof(vm.ContainerStats), 32));
        content.Children.Add(ReadOnlyBox(vm, nameof(vm.ContainerLogs), 140));
        return card;
    }

    private static Control Stacks(DockerManagerViewModel vm)
    {
        var card = Card(LocalizedText.Get("docker.stacks"), LocalizedText.Get("docker.stacks_hint")); var content = (StackPanel)card.Child!;
        content.Children.Add(TextField(vm, nameof(vm.StackName), "docker.stack.name", "my-stack", 350));
        content.Children.Add(TextField(vm, nameof(vm.ComposeYaml), "docker.stack.compose", "services:\n  web:\n    image: nginx:latest", 720, true, 270));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.stack.validate"), Command = vm.ValidateStackCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.stack.deploy"), Command = vm.DeployStackCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.stack.down"), Command = vm.DownStackCommand, Foreground = Brush.Parse("#B42318") });
        content.Children.Add(actions); return card;
    }

    private static Control Images(DockerManagerViewModel vm, Func<Task> showPullDialog)
    {
        var card = Card(LocalizedText.Get("docker.images"), LocalizedText.Get("docker.images_hint")); var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("docker.image.pull", showPullDialog));
        content.Children.Add(Separator()); content.Children.Add(ImageTable(vm));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        var confirm = new CheckBox { Content = LocalizedText.Get("docker.image.delete_confirm"), VerticalAlignment = VerticalAlignment.Center };
        confirm.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(vm.ConfirmImageDeletion)) { Mode = BindingMode.TwoWay }); actions.Children.Add(confirm);
        actions.Children.Add(new Button { Content = LocalizedText.Get("docker.image.delete"), Command = vm.DeleteImageCommand, Foreground = Brush.Parse("#B42318") }); content.Children.Add(actions);
        return card;
    }

    private static Control Networks(DockerManagerViewModel vm, Func<Task> showCreateDialog)
    {
        var card = Card(LocalizedText.Get("docker.networks"), LocalizedText.Get("docker.networks_hint")); var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("common.create", showCreateDialog));
        content.Children.Add(Separator()); content.Children.Add(NetworkTable(vm));
        var actions = ConfirmationAction(vm, nameof(vm.ConfirmNetworkDeletion), "docker.network.delete_confirm", vm.DeleteNetworkCommand); content.Children.Add(actions);
        return card;
    }

    private static Control Volumes(DockerManagerViewModel vm, Func<Task> showCreateDialog)
    {
        var card = Card(LocalizedText.Get("docker.volumes"), LocalizedText.Get("docker.volumes_hint")); var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("common.create", showCreateDialog));
        content.Children.Add(Separator()); content.Children.Add(VolumeTable(vm));
        content.Children.Add(ConfirmationAction(vm, nameof(vm.ConfirmVolumeDeletion), "docker.volume.delete_confirm", vm.DeleteVolumeCommand)); return card;
    }

    private static Task ShowCreateContainerDialogAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, LocalizedText.Get("docker.container.create"), dialog => CreateContainerDialog(vm, dialog), new RemoteOS.Core.Primitives.Size(720, 650));

    private static Task ShowPullImageDialogAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, LocalizedText.Get("docker.image.pull"), dialog => CreatePullImageDialog(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 230));

    private static Task ShowCreateNetworkDialogAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, LocalizedText.Get("common.create"), dialog => CreateNetworkDialog(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 280));

    private static Task ShowCreateVolumeDialogAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, LocalizedText.Get("common.create"), dialog => CreateVolumeDialog(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 280));

    private static Control CreateContainerDialog(DockerManagerViewModel vm, ModalDialog<bool> dialog)
    {
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(20), DataContext = vm };
        content.Children.Add(new TextBlock { Text = LocalizedText.Get("docker.containers_hint"), TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#61708B") });
        var form = new WrapPanel { ItemWidth = 220, ItemHeight = 68, Orientation = Orientation.Horizontal };
        form.Children.Add(TextField(vm, nameof(vm.ContainerName), "docker.container.name", "", 210));
        form.Children.Add(TextField(vm, nameof(vm.ContainerImage), "docker.container.image", "nginx:latest", 210));
        form.Children.Add(ChoiceField(vm, nameof(vm.ContainerNetwork), vm.AvailableNetworks, "docker.container.network", 210));
        form.Children.Add(ChoiceField(vm, nameof(vm.ContainerRestartPolicy), vm.RestartPolicies, "docker.container.restart", 210));
        form.Children.Add(TextField(vm, nameof(vm.ContainerPorts), "docker.container.ports", "8080:80", 210));
        form.Children.Add(TextField(vm, nameof(vm.ContainerMounts), "docker.container.mounts", "volume:/data", 210));
        content.Children.Add(form);
        content.Children.Add(TextField(vm, nameof(vm.ContainerEnvironment), "docker.container.environment", "KEY=value", 450, true));
        content.Children.Add(TextField(vm, nameof(vm.ContainerArguments), "docker.container.arguments", "", 450, true));
        content.Children.Add(DialogActions(dialog, "docker.container.create", vm.TryCreateContainerAsync));
        return new ScrollViewer { Content = content };
    }

    private static Control CreatePullImageDialog(DockerManagerViewModel vm, ModalDialog<bool> dialog)
    {
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(20), DataContext = vm };
        content.Children.Add(TextField(vm, nameof(vm.ImageReference), "docker.image.reference", "nginx:latest", 420));
        content.Children.Add(DialogActions(dialog, "docker.image.pull", vm.TryPullImageAsync));
        return content;
    }

    private static Control CreateNetworkDialog(DockerManagerViewModel vm, ModalDialog<bool> dialog)
    {
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(20), DataContext = vm };
        content.Children.Add(TextField(vm, nameof(vm.NetworkName), "common.name", "", 420));
        content.Children.Add(ChoiceField(vm, nameof(vm.SelectedNetworkDriver), vm.NetworkDrivers, "docker.network.driver", 220));
        content.Children.Add(DialogActions(dialog, "common.create", vm.TryCreateNetworkAsync));
        return content;
    }

    private static Control CreateVolumeDialog(DockerManagerViewModel vm, ModalDialog<bool> dialog)
    {
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(20), DataContext = vm };
        content.Children.Add(TextField(vm, nameof(vm.VolumeName), "common.name", "", 420));
        content.Children.Add(ChoiceField(vm, nameof(vm.SelectedVolumeDriver), vm.VolumeDrivers, "docker.volume.driver", 220));
        content.Children.Add(DialogActions(dialog, "common.create", vm.TryCreateVolumeAsync));
        return content;
    }

    private static Button CreateDialogButton(string textKey, Func<Task> showDialog)
    {
        var button = new Button { Content = LocalizedText.Get(textKey), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => await showDialog();
        return button;
    }

    private static Control DialogActions(ModalDialog<bool> dialog, string confirmTextKey, Func<Task<bool>> submit)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
        cancel.Click += (_, _) => dialog.Cancel();
        var confirm = new Button { Content = LocalizedText.Get(confirmTextKey), Classes = { "primary" } };
        confirm.Click += async (_, _) => { if (await submit()) dialog.Close(true); };
        actions.Children.Add(cancel); actions.Children.Add(confirm);
        return actions;
    }

    private static Border Card(string title, string hint)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#163059") });
        content.Children.Add(new TextBlock { Text = hint, Foreground = Brush.Parse("#61708B"), TextWrapping = TextWrapping.Wrap });
        return new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = content };
    }
    private static Border MetricCard(string label, string valueBinding, string description)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock { Text = label, Foreground = Brush.Parse("#61708B"), FontSize = 12 });
        var value = new TextBlock { FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#163059") }; value.Bind(TextBlock.TextProperty, new Binding(valueBinding)); content.Children.Add(value);
        content.Children.Add(new TextBlock { Text = description, Foreground = Brush.Parse("#61708B"), TextWrapping = TextWrapping.Wrap });
        return new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = content };
    }
    private static void AddSummaryCount(Grid grid, string title, string collectionBinding, int column)
    {
        var stack = new StackPanel { Spacing = 3 }; stack.Children.Add(new TextBlock { Text = title, Foreground = Brush.Parse("#61708B") });
        var value = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#163059") };
        value.Bind(TextBlock.TextProperty, new Binding($"{collectionBinding}.Count")); stack.Children.Add(value); Grid.SetColumn(stack, column); grid.Children.Add(stack);
    }
    private static Control Separator() => new Border { Height = 1, Background = Brush.Parse("#E5EAF2"), Margin = new Thickness(0, 2) };
    private static Control TextField(DockerManagerViewModel vm, string property, string labelKey, string placeholder, double width, bool multiline = false, double minHeight = 0)
    {
        var field = new StackPanel { Spacing = 4, Width = width, Margin = new Thickness(0, 0, 10, 4) }; field.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey), Foreground = Brush.Parse("#36506F") });
        var box = new TextBox { PlaceholderText = placeholder, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = minHeight };
        box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay }); field.Children.Add(box); return field;
    }
    private static Control ChoiceField(DockerManagerViewModel vm, string property, IEnumerable<string> options, string labelKey, double width)
    {
        var field = new StackPanel { Spacing = 4, Width = width, Margin = new Thickness(0, 0, 10, 4) }; field.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey), Foreground = Brush.Parse("#36506F") });
        var box = new ComboBox { ItemsSource = options }; box.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(property) { Mode = BindingMode.TwoWay }); field.Children.Add(box); return field;
    }
    private static Control ReadOnlyBox(DockerManagerViewModel vm, string property, double height)
    {
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = height, MaxHeight = height };
        box.Bind(TextBox.TextProperty, new Binding(property)); return box;
    }
    private static Control ConfirmationAction(DockerManagerViewModel vm, string property, string confirmationKey, System.Windows.Input.ICommand command)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        var check = new CheckBox { Content = LocalizedText.Get(confirmationKey), VerticalAlignment = VerticalAlignment.Center }; check.Bind(ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        actions.Children.Add(check); actions.Children.Add(new Button { Content = LocalizedText.Get("common.delete"), Command = command, Foreground = Brush.Parse("#B42318") }); return actions;
    }
    private static DataGrid ContainerTable(DockerManagerViewModel vm)
    {
        var table = Table(vm.Containers); table.Columns.Add(Column("Name", nameof(DockerContainerDto.Names), 180)); table.Columns.Add(Column("Image", nameof(DockerContainerDto.Image), 210)); table.Columns.Add(Column("Status", nameof(DockerContainerDto.Status), 260)); table.Columns.Add(Column("State", nameof(DockerContainerDto.State), 100));
        table.SelectionChanged += (_, _) => vm.SelectedContainer = table.SelectedItem as DockerContainerDto; return table;
    }
    private static DataGrid ImageTable(DockerManagerViewModel vm)
    {
        var table = Table(vm.Images); table.Columns.Add(Column("Repository", nameof(DockerImageDto.Repository), 240)); table.Columns.Add(Column("Tag", nameof(DockerImageDto.Tag), 120)); table.Columns.Add(Column("Size", nameof(DockerImageDto.Size), 100)); table.Columns.Add(Column("Created", nameof(DockerImageDto.CreatedSince), 180));
        table.SelectionChanged += (_, _) => vm.SelectedImage = table.SelectedItem as DockerImageDto; return table;
    }
    private static DataGrid NetworkTable(DockerManagerViewModel vm)
    {
        var table = Table(vm.Networks); table.Columns.Add(Column("Name", nameof(DockerNetworkDto.Name), 240)); table.Columns.Add(Column("Driver", nameof(DockerNetworkDto.Driver), 160)); table.Columns.Add(Column("Scope", nameof(DockerNetworkDto.Scope), 140));
        table.SelectionChanged += (_, _) => vm.SelectedNetwork = table.SelectedItem as DockerNetworkDto; return table;
    }
    private static DataGrid VolumeTable(DockerManagerViewModel vm)
    {
        var table = Table(vm.Volumes); table.Columns.Add(Column("Name", nameof(DockerVolumeDto.Name), 220)); table.Columns.Add(Column("Driver", nameof(DockerVolumeDto.Driver), 140)); table.Columns.Add(Column("Mount point", nameof(DockerVolumeDto.Mountpoint), 400));
        table.SelectionChanged += (_, _) => vm.SelectedVolume = table.SelectedItem as DockerVolumeDto; return table;
    }
    private static DataGrid Table(System.Collections.IEnumerable source) => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserReorderColumns = false, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Single, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 230, ItemsSource = source };
    private static DataGridTextColumn Column(string header, string property, double width) => new() { Header = header, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel) };
}
