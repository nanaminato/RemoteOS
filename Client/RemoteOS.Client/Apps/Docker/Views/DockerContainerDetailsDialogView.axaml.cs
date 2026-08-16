using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteOS.WindowManager;

namespace Client.Apps.Docker.Views;

internal partial class DockerContainerDetailsDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public DockerContainerDetailsDialogView(DockerManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
