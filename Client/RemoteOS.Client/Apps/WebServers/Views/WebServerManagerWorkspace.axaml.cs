using Avalonia.Controls;

namespace Client.Apps.WebServers.Views;

/// <summary>Web Server Manager shell. Layout lives in AXAML; this class only attaches the view model.</summary>
internal partial class WebServerManagerWorkspace : UserControl
{
    private WebServerManagerWorkspace()
    {
        InitializeComponent();
    }

    public static Control Create(WebServerManagerViewModel viewModel)
    {
        var view = new WebServerManagerWorkspace
        {
            DataContext = viewModel,
        };
        return view;
    }
}
