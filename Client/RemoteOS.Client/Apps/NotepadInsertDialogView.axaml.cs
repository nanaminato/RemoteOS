using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps;

public partial class NotepadInsertDialogView : UserControl
{
    public NotepadInsertDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => InputBox.Focus();
}
