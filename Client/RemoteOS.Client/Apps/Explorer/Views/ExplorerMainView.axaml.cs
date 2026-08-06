using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Client.Apps.Explorer.ViewModels;

namespace Client.Apps.Explorer.Views;

public partial class ExplorerMainView : UserControl
{
    private const double NameColumnMinWidth = 180;
    private const double SizeColumnMinWidth = 96;
    private const double ModifiedColumnMinWidth = 150;
    private const double TypeColumnMinWidth = 96;

    private string? _resizingColumn;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private double _nameColumnWidth = 260;
    private double _sizeColumnWidth = 110;
    private double _modifiedColumnWidth = 160;
    private double _typeColumnWidth = 110;

    public ExplorerMainView()
    {
        InitializeComponent();
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    public static readonly DirectProperty<ExplorerMainView, double> NameColumnWidthProperty =
        AvaloniaProperty.RegisterDirect<ExplorerMainView, double>(nameof(NameColumnWidth), o => o.NameColumnWidth);
    public static readonly DirectProperty<ExplorerMainView, double> SizeColumnWidthProperty =
        AvaloniaProperty.RegisterDirect<ExplorerMainView, double>(nameof(SizeColumnWidth), o => o.SizeColumnWidth);
    public static readonly DirectProperty<ExplorerMainView, double> ModifiedColumnWidthProperty =
        AvaloniaProperty.RegisterDirect<ExplorerMainView, double>(nameof(ModifiedColumnWidth), o => o.ModifiedColumnWidth);
    public static readonly DirectProperty<ExplorerMainView, double> TypeColumnWidthProperty =
        AvaloniaProperty.RegisterDirect<ExplorerMainView, double>(nameof(TypeColumnWidth), o => o.TypeColumnWidth);

    public double NameColumnWidth { get => _nameColumnWidth; private set => SetAndRaise(NameColumnWidthProperty, ref _nameColumnWidth, value); }
    public double SizeColumnWidth { get => _sizeColumnWidth; private set => SetAndRaise(SizeColumnWidthProperty, ref _sizeColumnWidth, value); }
    public double ModifiedColumnWidth { get => _modifiedColumnWidth; private set => SetAndRaise(ModifiedColumnWidthProperty, ref _modifiedColumnWidth, value); }
    public double TypeColumnWidth { get => _typeColumnWidth; private set => SetAndRaise(TypeColumnWidthProperty, ref _typeColumnWidth, value); }

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

    private void ColumnResizer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string column } resizer || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _resizingColumn = column;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = GetColumnWidth(column);
        e.Pointer.Capture(resizer);
        e.Handled = true;
    }

    private void ColumnResizer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingColumn is null || sender is not Control resizer || e.Pointer.Captured != resizer) return;
        SetColumnWidth(_resizingColumn, _resizeStartWidth + e.GetPosition(this).X - _resizeStartX);
        e.Handled = true;
    }

    private void ColumnResizer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control resizer && e.Pointer.Captured == resizer) e.Pointer.Capture(null);
        _resizingColumn = null;
        e.Handled = true;
    }

    private double GetColumnWidth(string column) => column switch
    {
        "Name" => NameColumnWidth,
        "Size" => SizeColumnWidth,
        "Modified" => ModifiedColumnWidth,
        "Type" => TypeColumnWidth,
        _ => 0
    };

    private void SetColumnWidth(string column, double width)
    {
        switch (column)
        {
            case "Name": NameColumnWidth = Math.Max(NameColumnMinWidth, width); break;
            case "Size": SizeColumnWidth = Math.Max(SizeColumnMinWidth, width); break;
            case "Modified": ModifiedColumnWidth = Math.Max(ModifiedColumnMinWidth, width); break;
            case "Type": TypeColumnWidth = Math.Max(TypeColumnMinWidth, width); break;
        }
    }
}
