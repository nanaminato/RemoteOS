using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace Client.Apps.Registry;

public partial class RegistryView : UserControl
{
    public RegistryView() => InitializeComponent();
    private void EntriesGrid_DoubleTapped(object? sender, RoutedEventArgs e) =>
        (DataContext as RegistryViewModel)?.EditSelectedCommand.Execute(null);
    private void NewValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.NewValueCommand.Execute(null);
    private void ModifyValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.EditSelectedCommand.Execute(null);
    private void DeleteValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.DeleteCommand.Execute(null);
    private void NavigationPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        (DataContext as RegistryViewModel)?.NavigateCommand.Execute(null);
        e.Handled = true;
    }
}
