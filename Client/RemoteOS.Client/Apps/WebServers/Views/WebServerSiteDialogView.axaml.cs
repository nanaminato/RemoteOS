using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteOS.WindowManager;

namespace Client.Apps.WebServers.Views;

internal partial class WebServerSiteDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public WebServerSiteDialogView(WebServerManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
}
