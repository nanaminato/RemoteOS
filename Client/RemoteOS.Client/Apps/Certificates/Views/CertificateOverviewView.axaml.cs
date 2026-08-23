using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Client.Apps.Certificates.Views;

internal partial class CertificateOverviewView : UserControl
{
    private readonly Func<Task> _showRequestCertificate;

    public CertificateOverviewView(Func<Task> showRequestCertificate)
    {
        _showRequestCertificate = showRequestCertificate;
        InitializeComponent();
    }

    private async void RequestCertificate_Click(object? sender, RoutedEventArgs e) => await _showRequestCertificate();
}
