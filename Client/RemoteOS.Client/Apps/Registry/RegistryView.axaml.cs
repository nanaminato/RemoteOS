using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps.Registry;

public partial class RegistryView : UserControl
{
    public RegistryView() => InitializeComponent();
    private void EntriesGrid_DoubleTapped(object? sender, RoutedEventArgs e) =>
        (DataContext as RegistryViewModel)?.EditSelectedCommand.Execute(null);
}
