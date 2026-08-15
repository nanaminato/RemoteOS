using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Localization;

namespace Client.Apps.Docker.Views;

/// <summary>Navigation shell and independently tweakable Docker Manager workspace panes.</summary>
internal static class DockerManagerWorkspace
{
    public static Control Create(DockerManagerViewModel vm, Func<Task> showCreateContainer, Func<Task> showDeployStack, Func<Task> showPullImage, Func<Task> showCreateNetwork, Func<Task> showCreateVolume)
    {
        var root = new Grid { DataContext = vm, Background = Brush.Parse("#F4F7FB"), RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(CreateHeader(vm));
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
        Grid.SetRow(body, 1); root.Children.Add(body);
        var navigation = new StackPanel { Spacing = 4, Margin = new Thickness(14, 22) };
        body.Children.Add(new Border { Background = Brush.Parse("#EEF3FA"), BorderBrush = Brush.Parse("#DCE5F1"), BorderThickness = new Thickness(0, 0, 1, 0), Child = navigation });
        var content = new ContentControl { Margin = new Thickness(30, 24) };
        Grid.SetColumn(content, 1); body.Children.Add(new ScrollViewer { Content = content });

        Button? selected = null;
        void Show(string section, Button button)
        {
            if (selected is not null) { selected.Background = Brushes.Transparent; selected.Foreground = Brush.Parse("#36506F"); }
            selected = button; button.Background = Brush.Parse("#DCEBFF"); button.Foreground = Brush.Parse("#1769D9");
            content.Content = section switch
            {
                "overview" => Overview(vm),
                "containers" => Containers(vm, showCreateContainer),
                "stacks" => Stacks(vm, showDeployStack),
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
            button.Click += (_, _) => Show(id, button); navigation.Children.Add(button);
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
        var status = new TextBlock { FontSize = 12, Foreground = Brush.Parse("#C8F6DD") }; status.Bind(TextBlock.TextProperty, new Binding(nameof(vm.StatusText)));
        var pill = new Border { Background = Brush.Parse("#1C3765"), CornerRadius = new CornerRadius(16), Padding = new Thickness(11, 5), VerticalAlignment = VerticalAlignment.Center, Child = status };
        Grid.SetColumn(pill, 2); header.Children.Add(pill);
        return new Border { Background = Brush.Parse("#122344"), Padding = new Thickness(28, 18), Child = header };
    }

    private static Control Overview(DockerManagerViewModel vm)
    {
        var layout = new StackPanel { Spacing = 18 };
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 18 };
        cards.Children.Add(MetricCardWithBoundDescription("docker.engine", nameof(vm.EngineVersion), nameof(vm.EnginePlatform)));
        var running = MetricCard("docker.running_containers", nameof(vm.RunningContainerCount), LocalizedText.Get("docker.running_hint")); Grid.SetColumn(running, 1); cards.Children.Add(running); layout.Children.Add(cards);
        var summary = Card("docker.overview", "docker.overview_hint"); var contents = (StackPanel)summary.Child!;
        var counts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        AddSummaryCount(counts, "docker.containers", nameof(vm.Containers), 0); AddSummaryCount(counts, "docker.stacks", nameof(vm.Stacks), 1); AddSummaryCount(counts, "docker.images", nameof(vm.Images), 2); AddSummaryCount(counts, "docker.networks", nameof(vm.Networks), 3); AddSummaryCount(counts, "docker.volumes", nameof(vm.Volumes), 4);
        contents.Children.Add(counts); contents.Children.Add(new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }); layout.Children.Add(summary);
        layout.Children.Add(new Border { Background = Brush.Parse("#EDF4FF"), BorderBrush = Brush.Parse("#CFE1FF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Child = new TextBlock { Text = LocalizedText.Get("docker.safety_hint"), Foreground = Brush.Parse("#315582"), TextWrapping = TextWrapping.Wrap } });
        return layout;
    }

    private static Control Containers(DockerManagerViewModel vm, Func<Task> showCreateDialog)
    {
        var card = Card("docker.containers", "docker.containers_hint"); var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("docker.container.create", showCreateDialog)); content.Children.Add(Separator()); content.Children.Add(DockerManagerTables.Containers(vm));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), ItemHeight = 34, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (key, command) in new[] { ("docker.action.start", vm.StartContainerCommand), ("docker.action.stop", vm.StopContainerCommand), ("docker.action.restart", vm.RestartContainerCommand), ("docker.action.pause", vm.PauseContainerCommand), ("docker.action.unpause", vm.UnpauseContainerCommand), ("docker.container.logs", vm.LoadContainerLogsCommand), ("docker.container.stats", vm.LoadContainerStatsCommand) }) actions.Children.Add(new Button { Content = LocalizedText.Get(key), Command = command });
        var confirmation = new CheckBox { Content = LocalizedText.Get("docker.container.delete_confirm"), VerticalAlignment = VerticalAlignment.Center }; confirmation.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(vm.ConfirmContainerDeletion)) { Mode = BindingMode.TwoWay }); actions.Children.Add(confirmation); actions.Children.Add(new Button { Content = LocalizedText.Get("common.delete"), Command = vm.DeleteContainerCommand, Foreground = Brush.Parse("#B42318") });
        foreach (Control action in actions.Children) action.Margin = new Thickness(0, 0, 8, 8); content.Children.Add(actions); content.Children.Add(ReadOnlyBox(vm, nameof(vm.ContainerStats), 32)); content.Children.Add(ReadOnlyBox(vm, nameof(vm.ContainerLogs), 140)); return card;
    }

    private static Control Stacks(DockerManagerViewModel vm, Func<Task> showDeployDialog)
    {
        var card = Card("docker.stacks", "docker.stacks_hint"); var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("docker.stack.deploy", showDeployDialog)); content.Children.Add(Separator()); content.Children.Add(DockerManagerTables.Stacks(vm));
        return card;
    }

    private static Control Images(DockerManagerViewModel vm, Func<Task> showPullDialog)
    {
        var card = Card("docker.images", "docker.images_hint"); var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("docker.image.pull", showPullDialog)); content.Children.Add(Separator()); content.Children.Add(DockerManagerTables.Images(vm));
        content.Children.Add(ConfirmationAction(vm, nameof(vm.ConfirmImageDeletion), "docker.image.delete_confirm", vm.DeleteImageCommand, "docker.image.delete")); return card;
    }
    private static Control Networks(DockerManagerViewModel vm, Func<Task> showCreateDialog)
    {
        var card = Card("docker.networks", "docker.networks_hint"); var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("common.create", showCreateDialog)); content.Children.Add(Separator()); content.Children.Add(DockerManagerTables.Networks(vm)); content.Children.Add(ConfirmationAction(vm, nameof(vm.ConfirmNetworkDeletion), "docker.network.delete_confirm", vm.DeleteNetworkCommand)); return card;
    }
    private static Control Volumes(DockerManagerViewModel vm, Func<Task> showCreateDialog)
    {
        var card = Card("docker.volumes", "docker.volumes_hint"); var content = (StackPanel)card.Child!;
        content.Children.Add(CreateDialogButton("common.create", showCreateDialog)); content.Children.Add(Separator()); content.Children.Add(DockerManagerTables.Volumes(vm)); content.Children.Add(ConfirmationAction(vm, nameof(vm.ConfirmVolumeDeletion), "docker.volume.delete_confirm", vm.DeleteVolumeCommand)); return card;
    }

    private static Border Card(string titleKey, string hintKey) => Card(LocalizedText.Get(titleKey), LocalizedText.Get(hintKey));
    private static Border Card(string title, string hint)
    {
        var content = new StackPanel { Spacing = 12 }; content.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#163059") }); content.Children.Add(new TextBlock { Text = hint, Foreground = Brush.Parse("#61708B"), TextWrapping = TextWrapping.Wrap }); return new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = content };
    }
    private static Border MetricCard(string labelKey, string valueBinding, string description)
    {
        var content = new StackPanel { Spacing = 6 }; content.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey), Foreground = Brush.Parse("#61708B"), FontSize = 12 }); var value = new TextBlock { FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#163059") }; value.Bind(TextBlock.TextProperty, new Binding(valueBinding)); content.Children.Add(value); content.Children.Add(new TextBlock { Text = description, Foreground = Brush.Parse("#61708B"), TextWrapping = TextWrapping.Wrap }); return new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = content };
    }
    private static Border MetricCardWithBoundDescription(string labelKey, string valueBinding, string descriptionBinding)
    {
        var content = new StackPanel { Spacing = 6 }; content.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey), Foreground = Brush.Parse("#61708B"), FontSize = 12 }); var value = new TextBlock { FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#163059") }; value.Bind(TextBlock.TextProperty, new Binding(valueBinding)); content.Children.Add(value); var description = new TextBlock { Foreground = Brush.Parse("#61708B"), TextWrapping = TextWrapping.Wrap }; description.Bind(TextBlock.TextProperty, new Binding(descriptionBinding)); content.Children.Add(description); return new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = content };
    }
    private static void AddSummaryCount(Grid grid, string titleKey, string collectionBinding, int column) { var stack = new StackPanel { Spacing = 3 }; stack.Children.Add(new TextBlock { Text = LocalizedText.Get(titleKey), Foreground = Brush.Parse("#61708B") }); var value = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#163059") }; value.Bind(TextBlock.TextProperty, new Binding($"{collectionBinding}.Count")); stack.Children.Add(value); Grid.SetColumn(stack, column); grid.Children.Add(stack); }
    private static Button CreateDialogButton(string key, Func<Task> action) { var button = new Button { Content = LocalizedText.Get(key), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += async (_, _) => await action(); return button; }
    private static Control Separator() => new Border { Height = 1, Background = Brush.Parse("#E5EAF2"), Margin = new Thickness(0, 2) };
    private static Control ReadOnlyBox(DockerManagerViewModel vm, string property, double height) { var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = height, MaxHeight = height }; box.Bind(TextBox.TextProperty, new Binding(property)); return box; }
    private static Control ConfirmationAction(DockerManagerViewModel vm, string property, string confirmationKey, System.Windows.Input.ICommand command, string actionKey = "common.delete") { var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) }; var check = new CheckBox { Content = LocalizedText.Get(confirmationKey), VerticalAlignment = VerticalAlignment.Center }; check.Bind(ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay }); actions.Children.Add(check); actions.Children.Add(new Button { Content = LocalizedText.Get(actionKey), Command = command, Foreground = Brush.Parse("#B42318") }); return actions; }
}
