using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps.Docker.Views;

internal partial class DockerContainersView : UserControl
{
    private readonly Func<Task> _showCreateDialog;

    public DockerContainersView(Func<Task> showCreateDialog)
    {
        _showCreateDialog = showCreateDialog;
        InitializeComponent();
    }

    private async void CreateContainer_Click(object? sender, RoutedEventArgs e) => await _showCreateDialog();
}
