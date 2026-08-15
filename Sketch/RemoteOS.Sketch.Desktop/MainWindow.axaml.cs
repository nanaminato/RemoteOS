using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteOS.Sketch.Protocol;
using System.Net.Http.Json;

namespace RemoteOS.Sketch.Desktop;
public partial class MainWindow : Window
{
    private readonly HttpClient _server = new() { BaseAddress = new Uri("http://127.0.0.1:5088") };
    public MainWindow() => InitializeComponent();
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            var response = await _server.PostAsJsonAsync("/api/mock/auth/login", new MockLoginRequest("design-user", "mock"));
            var login = await response.Content.ReadFromJsonAsync<MockLoginResponse>();
            ConnectionStatus.Text = login is null ? "Mock Server returned no login data" : $"Mock Server · connected as {login.DisplayName}";
        }
        catch (HttpRequestException) { ConnectionStatus.Text = "Mock Server offline · using built-in design data"; }
    }
    private void ShowOverview(object? sender, RoutedEventArgs e) => Show(OverviewPage);
    private void ShowDocker(object? sender, RoutedEventArgs e) => Show(DockerPage);
    private void ShowNginx(object? sender, RoutedEventArgs e) => Show(NginxPage);
    private void ShowCertificates(object? sender, RoutedEventArgs e) => Show(CertificatesPage);
    private void Show(Control page) { OverviewPage.IsVisible = DockerPage.IsVisible = NginxPage.IsVisible = CertificatesPage.IsVisible = false; page.IsVisible = true; }
}
