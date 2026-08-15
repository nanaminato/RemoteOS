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
        SketchLocalizer.Current.LanguageChanged += OnLanguageChanged;
        ApplyLanguage();
    }

    protected override void OnClosed(EventArgs e)
    {
        SketchLocalizer.Current.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            var response = await _server.PostAsJsonAsync("/api/mock/auth/login", new MockLoginRequest("design-user", "mock"));
            var login = await response.Content.ReadFromJsonAsync<MockLoginResponse>();
            ConnectionStatus.Text = login is null ? T("Local preview") : SketchLocalizer.Current.Format("Connected as {0}", login.DisplayName);
        }
        catch (HttpRequestException)
        {
            ConnectionStatus.Text = T("Offline design preview");
        }
    }

    private void OpenDocker(object? sender, RoutedEventArgs e) => new ManagerWindow(ManagerKind.Docker).Show();
    private void OpenNginx(object? sender, RoutedEventArgs e) => new ManagerWindow(ManagerKind.Nginx).Show();
    private void OpenCertificates(object? sender, RoutedEventArgs e) => new ManagerWindow(ManagerKind.Certificates).Show();

    private void ToggleLanguage(object? sender, RoutedEventArgs e) => SketchLocalizer.Current.ToggleLanguage();

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyLanguage();

    private void ApplyLanguage()
    {
        WorkspaceSubtitle.Text = T("Infrastructure workspace");
        WelcomeText.Text = T("Good to see you");
        DesktopTitle.Text = T("Server Desktop");
        DesktopHint.Text = T("Open a management app to work with your local services.");
        DockerTitle.Text = T("Docker Manager");
        DockerHint.Text = T("3 containers running");
        NginxTitle.Text = T("Nginx Manager");
        NginxHint.Text = T("1 site · service offline");
        CertificatesTitle.Text = T("Certificate Manager");
        CertificatesHint.Text = T("No ACME client detected");
        PreviewText.Text = T("Design preview · Local mock service · design-user");
        SystemsText.Text = T("All systems available");
        ToolTip.SetTip(DockerButton, T("Docker Manager"));
        ToolTip.SetTip(DockerTaskbarButton, T("Docker Manager"));
        ToolTip.SetTip(NginxButton, T("Nginx Manager"));
        ToolTip.SetTip(NginxTaskbarButton, T("Nginx Manager"));
        ToolTip.SetTip(CertificatesButton, T("Certificate Manager"));
        ToolTip.SetTip(CertificatesTaskbarButton, T("Certificate Manager"));
        LanguageButton.Content = SketchLocalizer.Current.IsChinese ? T("SwitchToEnglish") : T("SwitchToChinese");
        ClockText.Text = DateTime.Now.ToString("ddd, HH:mm", SketchLocalizer.Current.Culture);
        if (ConnectionStatus.Text is "Connecting…" or "正在连接…") ConnectionStatus.Text = T("Connecting…");
    }

    private static string T(string englishText) => SketchLocalizer.Current.Text(englishText);
}
