using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps.Registry;

public partial class RegistryView : UserControl
{
    public RegistryView() => InitializeComponent();
    private void EntriesGrid_DoubleTapped(object? sender, RoutedEventArgs e) =>
        (DataContext as RegistryViewModel)?.EditSelectedCommand.Execute(null);
    private void NewValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.NewValueCommand.Execute(null);
    private void ModifyValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.EditSelectedCommand.Execute(null);
    private void DeleteValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.DeleteCommand.Execute(null);
}
