using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Client.Apps.WebServers.Views;

/// <summary>Web Server Manager shell with separate instance and site workspaces.</summary>
internal partial class WebServerManagerWorkspace : UserControl
{
    private Button? _selectedButton;

    private WebServerManagerWorkspace(WebServerManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ShowPage("instances", InstancesPageButton);
    }

    public static Control Create(WebServerManagerViewModel viewModel) => new WebServerManagerWorkspace(viewModel);

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } button)
            ShowPage(page, button);
    }

    private void ShowPage(string page, Button button)
    {
        if (_selectedButton is not null)
        {
            _selectedButton.Background = Brushes.Transparent;
            _selectedButton.Foreground = Brush.Parse("#36506F");
        }

        _selectedButton = button;
        button.Background = Brush.Parse("#DCEBFF");
        button.Foreground = Brush.Parse("#1769D9");
        InstancesPage.IsVisible = page == "instances";
        SitesPage.IsVisible = page == "sites";
    }
}
