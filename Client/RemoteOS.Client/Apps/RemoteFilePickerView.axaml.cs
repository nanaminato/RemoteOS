using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps;

public partial class RemoteFilePickerView : UserControl
{
    public RemoteFilePickerView() => InitializeComponent();

    private void Entries_DoubleTapped(object? sender, RoutedEventArgs e)
        => _ = (DataContext as RemoteFilePickerViewModel)?.OpenSelectedCommand.ExecuteAsync(null);
}
