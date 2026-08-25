using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps.Tunnels.Views;

/// <summary>Shell for the tunnel manager. The individual views inherit one shared view model.</summary>
public partial class TunnelManagerView : UserControl
{
    private Button? _selectedButton;
    public TunnelManagerView()
    {
        InitializeComponent();
        ShowPage("overview", OverviewButton);
    }

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } button) ShowPage(section, button);
    }

    private void ShowPage(string section, Button button)
    {
        _selectedButton?.Classes.Remove("nav-selected");
        _selectedButton = button;
        button.Classes.Add("nav-selected");
        ContentHost.Content = section switch
        {
            "tunnels" => new TunnelDefinitionsView(),
            "servers" => new TunnelServersView(),
            "runtime" => new TunnelRuntimeView(),
            _ => new TunnelOverviewView()
        };
    }
}
