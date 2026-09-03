using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteOS.WindowManager;

namespace Client.Apps.Proxy.Views;

internal partial class ProxyNetworkSettingsDialogView : UserControl
{
    private readonly ModalDialog<bool> dialog;

    public ProxyNetworkSettingsDialogView(ProxyManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        this.dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => dialog.Cancel();

    private async void Save_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ProxyManagerViewModel viewModel && viewModel.SaveSettingsCommand.CanExecute(null))
            await viewModel.SaveSettingsCommand.ExecuteAsync(null);
        dialog.Close(true);
    }
}
