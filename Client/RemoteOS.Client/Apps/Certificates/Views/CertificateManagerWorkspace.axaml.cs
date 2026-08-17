using Avalonia.Controls;

namespace Client.Apps.Certificates.Views;

/// <summary>Certificate Manager shell. Layout lives in AXAML; this class only attaches the view model.</summary>
internal partial class CertificateManagerWorkspace : UserControl
{
    private CertificateManagerWorkspace()
    {
        InitializeComponent();
    }

    public static Control Create(CertificateManagerViewModel viewModel)
    {
        var view = new CertificateManagerWorkspace
        {
            DataContext = viewModel,
        };
        return view;
    }
}
