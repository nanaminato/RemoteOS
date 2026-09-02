using Avalonia.Controls;
using Avalonia.Threading;

namespace Client.Apps.Proxy.Views;

internal partial class ProxyOverviewView : UserControl
{
    private readonly DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public ProxyOverviewView()
    {
        InitializeComponent();
        _trafficTimer.Tick += TrafficTimer_Tick;
        AttachedToVisualTree += (_, _) =>
        {
            _trafficTimer.Start();
            _ = RefreshTrafficAsync();
        };
        DetachedFromVisualTree += (_, _) => _trafficTimer.Stop();
    }

    private async void TrafficTimer_Tick(object? sender, EventArgs eventArgs) => await RefreshTrafficAsync();

    private Task RefreshTrafficAsync() => DataContext is ProxyManagerViewModel viewModel
        ? viewModel.RefreshTrafficAsync()
        : Task.CompletedTask;
}
