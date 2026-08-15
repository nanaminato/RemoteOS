using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteOS.Sketch.Protocol;
using System.Net.Http.Json;

namespace RemoteOS.Sketch.Client;

public partial class MainWindow : Window
{
    private readonly HttpClient _server = new() { BaseAddress = new Uri("http://127.0.0.1:5088") };

    public MainWindow()
    {
        InitializeComponent();
        ClockText.Text = DateTime.Now.ToString("ddd, HH:mm");
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            var response = await _server.PostAsJsonAsync("/api/mock/auth/login", new MockLoginRequest("design-user", "mock"));
            var login = await response.Content.ReadFromJsonAsync<MockLoginResponse>();
            ConnectionStatus.Text = login is null ? "Local preview" : $"Connected as {login.DisplayName}";
        }
        catch (HttpRequestException)
        {
            ConnectionStatus.Text = "Offline design preview";
        }
    }

    private void OpenDocker(object? sender, RoutedEventArgs e) => new ManagerWindow(ManagerKind.Docker).Show();
    private void OpenNginx(object? sender, RoutedEventArgs e) => new ManagerWindow(ManagerKind.Nginx).Show();
    private void OpenCertificates(object? sender, RoutedEventArgs e) => new ManagerWindow(ManagerKind.Certificates).Show();
}
