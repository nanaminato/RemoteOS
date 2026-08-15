using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RemoteOS.Sketch.Protocol;
using System.Net.Http.Json;

namespace RemoteOS.Sketch.Client;

public enum ManagerKind { Docker, Nginx, Certificates }

public partial class ManagerWindow : Window
{
    private readonly ManagerKind _kind;
    private readonly HttpClient _server = new() { BaseAddress = new Uri("http://127.0.0.1:5088") };
    private string _section = "Overview";

    public ManagerWindow() : this(ManagerKind.Docker) { }

    public ManagerWindow(ManagerKind kind)
    {
        _kind = kind;
        InitializeComponent();
        Configure();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await LoadFromServerAsync();
    }

    private void Configure()
    {
        switch (_kind)
        {
            case ManagerKind.Docker:
                Title = "Docker Manager · RemoteOS";
                AppMark.Background = Brush.Parse("#147CB8");
                AppMarkText.Text = "🐳";
                TitleText.Text = "Docker Manager";
                SubtitleText.Text = "Containers, images and local workloads";
                StatusPill.Text = "●  Engine running";
                ServiceStatusText.Text = "Docker Engine is running";
                ServiceDetailText.Text = "Version 27.1.1 · 3 containers detected";
                MetricLabel.Text = "Running containers";
                MetricValue.Text = "3";
                MetricDescription.Text = "1 stopped container is available";
                SetNavigation("Overview", "Containers", "Stacks", "Images", "Networks", "Volumes");
                GuidanceCard.IsVisible = false;
                break;

            case ManagerKind.Nginx:
                Title = "Nginx Manager · RemoteOS";
                AppMark.Background = Brush.Parse("#159B77");
                AppMarkText.Text = "N";
                TitleText.Text = "Nginx Manager";
                SubtitleText.Text = "Sites, reverse proxies and server configuration";
                StatusPill.Text = "●  Service offline";
                ServiceStatusText.Text = "Nginx is not installed";
                ServiceDetailText.Text = "Install and start Nginx before managing live sites.";
                MetricLabel.Text = "Configured sites";
                MetricValue.Text = "1";
                MetricDescription.Text = "One design-preview configuration";
                SetNavigation("Overview", "Sites", "Configuration", "Test & Reload", "Logs");
                GuidanceText.Text = "1. Review your platform and the official Nginx installation guide.\n2. An administrator installs and starts Nginx.\n3. Return here and refresh the service status.";
                break;

            default:
                Title = "Certificate Manager · RemoteOS";
                AppMark.Background = Brush.Parse("#8C55CB");
                AppMarkText.Text = "⌁";
                TitleText.Text = "Certificate Manager";
                SubtitleText.Text = "HTTPS certificates, renewal and domain validation";
                StatusPill.Text = "●  Setup needed";
                ServiceStatusText.Text = "Certificate service is not ready";
                ServiceDetailText.Text = "No supported ACME client was detected on this host.";
                MetricLabel.Text = "Tracked certificates";
                MetricValue.Text = "1";
                MetricDescription.Text = "One design-preview certificate";
                SetNavigation("Overview", "Certificates", "ACME accounts", "DNS providers", "Renewal policy");
                GuidanceText.Text = "1. Install an approved ACME client.\n2. Prepare DNS or HTTP-01 validation.\n3. Verify the service before issuing a certificate.\n\nPrivate keys and DNS credentials remain in server-side secure storage.";
                break;
        }
        DescribeSection();
    }

    private void SetNavigation(params string[] sections)
    {
        NavigationPanel.Children.Clear();
        foreach (var section in sections)
        {
            var button = new Button { Content = section, Tag = section };
            button.Classes.Add("manager-nav");
            button.Click += SelectSection;
            NavigationPanel.Children.Add(button);
        }
    }

    private async void SelectSection(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;
        _section = section;
        DescribeSection();
        await LoadFromServerAsync();
    }

    private void DescribeSection()
    {
        SectionTitle.Text = _section;
        SectionHint.Text = _section switch
        {
            "Overview" => "Health, key metrics and recent operations.",
            "Containers" => "Manage workload lifecycle and inspect current state.",
            "Stacks" => "Compose definitions, deployment state and source.",
            "Images" => "Local images, storage use and safe cleanup previews.",
            "Networks" => "Container networks and attached workloads.",
            "Volumes" => "Persistent storage and current consumers.",
            "Sites" => "Domains, upstreams, HTTPS bindings and publish state.",
            "Configuration" => "Versioned, reviewable server configuration snapshots.",
            "Test & Reload" => "Test syntax before applying a controlled reload.",
            "Logs" => "Recent access, error and configuration events.",
            "Certificates" => "Issued certificates, validity and automated renewal.",
            "ACME accounts" => "Account metadata and production directory status.",
            "DNS providers" => "Configured DNS validation providers; secrets stay masked.",
            "Renewal policy" => "Renewal threshold and safe maintenance window.",
            _ => "Resources in this workspace."
        };
        ActionButton.Content = _section == "Test & Reload" ? "Run configuration test" : "Refresh";
    }

    private async Task LoadFromServerAsync()
    {
        try
        {
            var prefix = _kind switch { ManagerKind.Docker => "docker", ManagerKind.Nginx => "nginx", _ => "certificates" };
            var overview = await _server.GetFromJsonAsync<ManagerOverview>($"/api/sketch/{prefix}/overview");
            if (overview is not null) ApplyOverview(overview);
            RowsPanel.Children.Clear();
            if (_section == "Overview" && overview is not null)
                foreach (var item in overview.RecentActivity) AddRow(item.Action, item.Target, item.OccurredAt.LocalDateTime.ToString("MMM dd, HH:mm"), item.Result, item.Result is "Succeeded" or "Passed" or "Queued");
            else await LoadSectionRowsAsync();
            DescribeSection();
        }
        catch (HttpRequestException)
        {
            RowsPanel.Children.Clear();
            AddRow("Mock Server offline", "Start RemoteOS.Sketch.Server", "127.0.0.1:5088", "Offline", false);
            ActionButton.Content = "Retry connection";
        }
    }

    private async Task LoadSectionRowsAsync()
    {
        switch (_kind)
        {
            case ManagerKind.Docker:
                if (_section == "Containers")
                {
                    var data = await _server.GetFromJsonAsync<PagedResult<DockerContainerSummary>>("/api/sketch/docker/containers");
                    if (data is not null) foreach (var item in data.Items) AddRow(item.Name, item.Image, item.Status, item.Ports, item.State == "running");
                }
                else if (_section == "Stacks")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<DockerStackSummary>>("/api/sketch/docker/stacks");
                    if (data is not null) foreach (var item in data) AddRow(item.Name, item.Source, $"{item.Services} services", item.Status, item.Status == "running");
                }
                else if (_section == "Images")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<DockerImageSummary>>("/api/sketch/docker/images");
                    if (data is not null) foreach (var item in data) AddRow($"{item.Repository}:{item.Tag}", item.Size, item.Created, item.InUse ? "In use" : "Unused", item.InUse);
                }
                else if (_section == "Networks")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<DockerNetworkSummary>>("/api/sketch/docker/networks");
                    if (data is not null) foreach (var item in data) AddRow(item.Name, item.Driver, $"{item.Containers} containers", "Available", true);
                }
                else if (_section == "Volumes")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<DockerVolumeSummary>>("/api/sketch/docker/volumes");
                    if (data is not null) foreach (var item in data) AddRow(item.Name, item.Driver, $"{item.Consumers} consumers", "Mounted", item.Consumers > 0);
                }
                break;
            case ManagerKind.Nginx:
                if (_section == "Sites")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<NginxSiteSummary>>("/api/sketch/nginx/sites");
                    if (data is not null) foreach (var item in data) AddRow(item.Name, item.Domains, item.Upstream, item.Enabled ? "Enabled" : "Disabled", item.Enabled);
                }
                else if (_section == "Configuration")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<NginxConfigSnapshot>>("/api/sketch/nginx/configuration/versions");
                    if (data is not null) foreach (var item in data) AddRow(item.Version, item.Summary, item.CreatedAt.LocalDateTime.ToString("MMM dd"), item.Author, true);
                }
                else if (_section == "Logs")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<NginxLogEntry>>("/api/sketch/nginx/logs");
                    if (data is not null) foreach (var item in data) AddRow(item.Site, item.Message, item.OccurredAt.LocalDateTime.ToString("HH:mm"), item.StatusCode?.ToString() ?? item.Level, item.Level != "warning");
                }
                else if (_section == "Test & Reload") AddRow("No test run yet", "Run the configuration test before reloading", "", "Pending", false);
                break;
            default:
                if (_section == "Certificates")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<CertificateSummary>>("/api/sketch/certificates/items");
                    if (data is not null) foreach (var item in data) AddRow(item.Domains, item.Issuer, $"Expires {item.ExpiresOn:yyyy-MM-dd}", item.Status, item.Status == "Valid");
                }
                else if (_section == "ACME accounts")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<AcmeAccountSummary>>("/api/sketch/certificates/acme-accounts");
                    if (data is not null) foreach (var item in data) AddRow(item.Email, "Let's Encrypt production", item.CreatedAt.LocalDateTime.ToString("MMM dd"), item.Status, item.Status == "Active");
                }
                else if (_section == "DNS providers")
                {
                    var data = await _server.GetFromJsonAsync<IReadOnlyList<DnsProviderSummary>>("/api/sketch/certificates/dns-providers");
                    if (data is not null) foreach (var item in data) AddRow(item.Name, item.CredentialReference, "Credentials masked", item.IsConfigured ? "Configured" : "Missing", item.IsConfigured);
                }
                else if (_section == "Renewal policy")
                {
                    var item = await _server.GetFromJsonAsync<CertificateRenewalPolicy>("/api/sketch/certificates/renewal-policy");
                    if (item is not null) AddRow("Automatic renewal", $"{item.DaysBeforeExpiry} days before expiry", item.PreferredWindow, item.Enabled ? "Enabled" : "Disabled", item.Enabled);
                }
                break;
        }
    }

    private void ApplyOverview(ManagerOverview overview)
    {
        ServiceStatusText.Text = overview.Headline;
        ServiceDetailText.Text = overview.Detail;
        StatusPill.Text = overview.Health switch { "healthy" => "●  Healthy", "attention" => "●  Attention", _ => "●  Unavailable" };
        if (overview.Metrics.Count > 0)
        {
            MetricLabel.Text = overview.Metrics[0].Label;
            MetricValue.Text = overview.Metrics[0].Value;
            MetricDescription.Text = overview.Metrics[0].Detail;
        }
        if (overview.RecentActivity.Count > 0)
        {
            var activity = overview.RecentActivity[0];
            GuidanceCard.IsVisible = true;
            GuidanceText.Text = $"Latest activity · {activity.OccurredAt.LocalDateTime:HH:mm}\n{activity.Action}: {activity.Target} — {activity.Result}";
        }
    }

    private void AddRow(string title, string detail, string value, string status, bool healthy)
    {
        var row = new Border
        {
            Background = Brush.Parse("#F8FAFD"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,1.35*,1.15*,Auto") }
        };
        var grid = (Grid)row.Child;
        grid.Children.Add(Label(title, "#203657", FontWeight.SemiBold));
        grid.Children.Add(Label(detail, "#62718A", FontWeight.Normal, 1));
        grid.Children.Add(Label(value, "#62718A", FontWeight.Normal, 2));
        grid.Children.Add(Label(status, healthy ? "#168451" : "#A65A25", FontWeight.SemiBold, 3));
        RowsPanel.Children.Add(row);
    }

    private static TextBlock Label(string text, string color, FontWeight weight, int column = 0)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brush.Parse(color),
            FontWeight = weight,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, column);
        return label;
    }

    private async void Refresh(object? sender, RoutedEventArgs e)
    {
        if (_kind == ManagerKind.Nginx && _section == "Test & Reload")
        {
            ActionButton.Content = "Testing…";
            try
            {
                var response = await _server.PostAsync("/api/sketch/nginx/configuration/test", null);
                var result = await response.Content.ReadFromJsonAsync<NginxTestResult>();
                RowsPanel.Children.Clear();
                if (result is not null) foreach (var message in result.Messages) AddRow("nginx -t", message, result.TestedAt.LocalDateTime.ToString("HH:mm"), result.Succeeded ? "Passed" : "Failed", result.Succeeded);
            }
            catch (HttpRequestException) { await LoadFromServerAsync(); }
            finally { DescribeSection(); }
            return;
        }
        ActionButton.Content = "Refreshing…";
        await LoadFromServerAsync();
    }
}
