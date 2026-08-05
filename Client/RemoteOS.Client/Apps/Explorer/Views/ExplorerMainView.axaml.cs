using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Client.Apps.Explorer.ViewModels;

namespace Client.Apps.Explorer.Views;

public partial class ExplorerMainView : UserControl
{
    public ExplorerMainView()
    {
        InitializeComponent();
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    /// <summary>地址栏回车跳转。</summary>
    private void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            _ = ViewModel?.AddressbarGoAsync(tb.Text);
            e.Handled = true;
        }
    }

    /// <summary>"转到"按钮点击。</summary>
    private void GoButton_Click(object? sender, RoutedEventArgs e)
        => _ = ViewModel?.AddressbarGoAsync(AddressBox.Text);

    /// <summary>列表双击：进入目录或下载文件。</summary>
    private void EntriesList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedEntry is { } entry)
            _ = ViewModel.InvokeEntryAsync(entry);
    }

    private void EntriesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list)
            ViewModel?.UpdatePickerSelection(list.SelectedItems?.Cast<object>() ?? []);
    }

    private void EntriesList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        for (var control = e.Source as Control; control is not null; control = control.GetVisualParent() as Control)
        {
            if (control.DataContext is RemoteOS.Protocol.Files.FileSystemEntryDto entry)
            {
                if (ViewModel is not null) ViewModel.SelectedEntry = entry;
                return;
            }
        }
    }
}
