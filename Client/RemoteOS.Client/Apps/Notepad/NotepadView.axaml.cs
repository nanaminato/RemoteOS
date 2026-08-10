using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps.Notepad;

public partial class NotepadView : UserControl
{
    public NotepadView()
    {
        InitializeComponent();
    }

    private void EncodingButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.ContextMenu is { } menu)
            menu.Open(control);
    }
}
