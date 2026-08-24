using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps.Certificates.Views;

/// <summary>Certificate Manager shell. Layout lives in AXAML; this class only attaches the view model.</summary>
internal partial class CertificateManagerWorkspace : UserControl
{
    private readonly Func<Task> _showRequestCertificate;
    private Button? _selectedButton;

    private CertificateManagerWorkspace(Func<Task> showRequestCertificate)
    {
        _showRequestCertificate = showRequestCertificate;
        InitializeComponent();
        ShowPage("overview", OverviewButton);
    }

    public static Control Create(CertificateManagerViewModel viewModel, Func<Task> showRequestCertificate)
    {
        var view = new CertificateManagerWorkspace(showRequestCertificate)
        {
            DataContext = viewModel,
        };
        return view;
    }

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } button)
            ShowPage(section, button);
    }

    private void ShowPage(string section, Button button)
    {
        if (_selectedButton is not null)
        {
            _selectedButton.Classes.Remove("nav-selected");
        }

        _selectedButton = button;
        button.Classes.Add("nav-selected");
        ContentHost.Content = section == "certificates"
            ? new CertificateListView(_showRequestCertificate)
            : new CertificateOverviewView(_showRequestCertificate);
    }
}
