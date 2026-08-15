using RemoteOS.Sketch.Protocol;

namespace RemoteOS.Sketch.Server;

/// <summary>
/// Deliberately in-memory design data. It behaves like a small control plane so UI flows can be
/// prototyped against stable, stateful endpoints without ever touching local Docker, Nginx or ACME.
/// </summary>
public sealed class SketchMockStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _installed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Docker"] = true,
        ["Nginx"] = false,
        ["Certificates"] = false
    };
    private readonly List<DockerContainerSummary> _containers =
    [
        new("c1b37c11a811", "remoteos-web", "nginx:1.27", "running", "Up 2 days", "80:80, 443:443", 1.7, "46.2 MiB / 512 MiB"),
        new("a92da2942fed", "remoteos-api", "remoteos/server:sketch", "running", "Up 2 days", "5000:8080", 4.2, "183 MiB / 1 GiB"),
        new("b3c19a6ff114", "postgres", "postgres:16", "exited", "Exited (0) 4 hours ago", "5432:5432", 0, "0 B / 1 GiB")
    ];
    private readonly List<DockerStackSummary> _stacks =
    [
        new("remoteos", "Compose editor", "running", 3, DateTimeOffset.UtcNow.AddHours(-2), "services:\n  web:\n    image: nginx:1.27\n    ports: [\"80:80\"]"),
        new("monitoring", "Template: Prometheus", "stopped", 2, DateTimeOffset.UtcNow.AddDays(-1), "services:\n  prometheus:\n    image: prom/prometheus")
    ];
    private readonly List<DockerImageSummary> _images =
    [
        new("sha256:7bc", "nginx", "1.27", "192 MB", "2 weeks ago", true),
        new("sha256:8aa", "remoteos/server", "sketch", "318 MB", "3 days ago", true),
        new("sha256:9df", "postgres", "16", "432 MB", "1 month ago", true),
        new("sha256:60b", "alpine", "3.20", "7.8 MB", "2 months ago", false)
    ];
    private readonly Dictionary<string, DockerContainerConfiguration> _containerConfigurations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c1b37c11a811"] = new("nginx", "", "80:80, 443:443", "", "", "bridge", "unless-stopped"),
        ["a92da2942fed"] = new("remoteos-api", "dotnet RemoteOS.Server.dll", "5000:8080", "ASPNETCORE_ENVIRONMENT=Production", "remoteos-data:/var/lib/remoteos", "remoteos_default", "unless-stopped"),
        ["b3c19a6ff114"] = new("postgres", "", "5432:5432", "POSTGRES_PASSWORD=••••••••", "postgres-data:/var/lib/postgresql/data", "bridge", "unless-stopped")
    };
    private readonly List<DockerNetworkSummary> _networks =
    [
        new("net_system_bridge", "bridge", "bridge", 2),
        new("net_01", "remoteos_default", "bridge", 1),
        new("net_02", "monitoring", "bridge", 0)
    ];
    private readonly List<DockerVolumeSummary> _volumes =
    [
        new("remoteos-data", "local", "/var/lib/docker/volumes/remoteos-data", 1),
        new("postgres-data", "local", "/var/lib/docker/volumes/postgres-data", 1)
    ];
    private readonly List<NginxSiteSummary> _sites =
    [
        new("site_01", "RemoteOS portal", "remoteos.local, www.remoteos.local", "http://remoteos-web:80", true, "cert_01", DateTimeOffset.UtcNow.AddMinutes(-42)),
        new("site_02", "Example API", "api.example.local", "http://remoteos-api:8080", false, "", DateTimeOffset.UtcNow.AddDays(-1))
    ];
    private readonly List<CertificateSummary> _certificates =
    [
        new("cert_01", "remoteos.local, www.remoteos.local", "Let's Encrypt", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(78)), "Valid", true, "HTTP-01"),
        new("cert_02", "api.example.local", "Let's Encrypt", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(12)), "Expiring soon", true, "DNS-01")
    ];
    private readonly List<ActivityItem> _dockerActivity = [];
    private readonly List<ActivityItem> _nginxActivity = [];
    private readonly List<ActivityItem> _certificateActivity = [];

    public SketchMockStore()
    {
        AddActivity(_dockerActivity, "Stack deployed", "remoteos", "Succeeded");
        AddActivity(_nginxActivity, "Configuration test", "RemoteOS portal", "Passed");
        AddActivity(_certificateActivity, "Renewal scheduled", "remoteos.local", "Queued");
    }

    public IReadOnlyList<ManagerStatus> Managers()
    {
        lock (_gate)
        {
            return
            [
                Status("Docker", "27.1.1", "Docker Engine is running.", ["Install Docker Engine", "Start the Docker service", "Refresh this preview"]),
                Status("Nginx", "1.27.0", "Configuration is available; service is offline.", ["Install or start Nginx", "Test configuration", "Reload after review"]),
                Status("Certificates", "ACME v2", "No supported ACME client was detected.", ["Install an ACME client", "Configure validation", "Issue a test certificate"])
            ];
        }
    }

    public MockOperationResult SetInstalled(string manager, bool isInstalled)
    {
        lock (_gate)
        {
            if (!_installed.ContainsKey(manager)) return Fail("Unknown manager.");
            _installed[manager] = isInstalled;
            return Ok($"{manager} is now simulated as {(isInstalled ? "installed" : "not installed")}.");
        }
    }

    public ManagerOverview DockerOverview()
    {
        lock (_gate)
        {
            if (!_installed["Docker"]) return NotInstalledOverview("Docker", "Docker is not installed", "Install Docker Engine to create and manage local workloads.");
            return new("Docker", "healthy", "Docker Engine is running", "Pull a public image, configure it, and run a local container.", [
                new("Running containers", _containers.Count(c => c.State == "running").ToString(), $"{_containers.Count(c => c.State != "running")} stopped container", "success"),
                new("Images", _images.Count.ToString(), "Public images available to run", "neutral"),
                new("Networks", _networks.Count.ToString(), "Container connectivity", "neutral"),
                new("Volumes", _volumes.Count.ToString(), "Persistent data", "neutral")], _dockerActivity.Take(6).ToArray());
        }
    }

    public PagedResult<DockerContainerSummary> Containers()
    {
        lock (_gate) return new(_containers.ToArray(), _containers.Count);
    }

    public DockerContainerDetail? Container(string id)
    {
        lock (_gate)
        {
            var item = _containers.SingleOrDefault(c => c.Id == id || c.Name == id);
            if (item is null) return null;
            var config = _containerConfigurations.GetValueOrDefault(item.Id) ?? new(item.Name, "", item.Ports, "", "", "bridge", "unless-stopped");
            return new(item.Id, item.Name, item.Image, item.State, item.Status, item.Ports, config.Command, config.Environment, config.Mounts, config.Network, config.RestartPolicy);
        }
    }

    public MockOperationResult CreateContainer(DockerContainerCreateRequest request)
    {
        lock (_gate)
        {
            if (!IsContainerNameValid(request.Name)) return Fail("Container names can use letters, numbers, dots, underscores and hyphens.");
            if (!IsPublicImageReference(request.Image)) return Fail("Enter a public image reference such as nginx:1.27 or redis:7.");
            var image = NormalizeImageReference(request.Image);
            if (_containers.Any(item => item.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))) return Fail("A container with this name already exists.");
            if (!_images.Any(item => ImageReference(item).Equals(image, StringComparison.OrdinalIgnoreCase))) return Fail("Pull this public image before running it.");
            if (!_networks.Any(item => item.Name.Equals(request.Network.Trim(), StringComparison.OrdinalIgnoreCase))) return Fail("Select an existing network or create one first.");

            var id = Guid.NewGuid().ToString("N")[..12];
            var state = request.StartImmediately ? "running" : "created";
            var status = request.StartImmediately ? "Up just now" : "Created";
            var container = new DockerContainerSummary(id, request.Name.Trim(), image, state, status, request.Ports.Trim(), 0, "0 B / no limit");
            _containers.Add(container);
            _containerConfigurations[id] = new(container.Name, request.Command.Trim(), container.Ports, request.Environment.Trim(), request.Mounts.Trim(), request.Network.Trim(), request.RestartPolicy.Trim());
            MarkImageInUse(container.Image);
            AddActivity(_dockerActivity, request.StartImmediately ? "Container created and started" : "Container created", container.Name, "Succeeded");
            return Ok($"Container {container.Name} was created{(request.StartImmediately ? " and started" : string.Empty)}.");
        }
    }

    public MockOperationResult UpdateContainer(string id, DockerContainerUpdateRequest request)
    {
        lock (_gate)
        {
            var index = _containers.FindIndex(item => item.Id == id || item.Name == id);
            if (index < 0) return Fail("Container not found.");
            if (!IsContainerNameValid(request.Name)) return Fail("Container names can use letters, numbers, dots, underscores and hyphens.");
            if (_containers.Where((_, currentIndex) => currentIndex != index).Any(item => item.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))) return Fail("A container with this name already exists.");
            if (!_networks.Any(item => item.Name.Equals(request.Network.Trim(), StringComparison.OrdinalIgnoreCase))) return Fail("Select an existing network or create one first.");

            var existing = _containers[index];
            var updated = existing with { Name = request.Name.Trim(), Ports = request.Ports.Trim(), Status = existing.State == "running" ? "Up just now · parameters updated" : existing.Status };
            _containers[index] = updated;
            _containerConfigurations[updated.Id] = new(updated.Name, request.Command.Trim(), updated.Ports, request.Environment.Trim(), request.Mounts.Trim(), request.Network.Trim(), request.RestartPolicy.Trim());
            AddActivity(_dockerActivity, "Container parameters updated", updated.Name, "Succeeded");
            return Ok($"Parameters for {updated.Name} were updated.");
        }
    }

    public MockOperationResult ContainerAction(string id, string action, bool confirmed)
    {
        lock (_gate)
        {
            var index = _containers.FindIndex(c => c.Id == id || c.Name == id);
            if (index < 0) return Fail("Container not found.");
            if (action is not ("start" or "stop" or "restart" or "pause" or "unpause" or "delete")) return Fail("Unsupported container action.");
            if (action is "delete" or "stop" && !confirmed) return Fail("Confirmation is required for this action.");
            var item = _containers[index];
            if (action == "delete") { _containers.RemoveAt(index); _containerConfigurations.Remove(item.Id); UpdateImageUsage(); AddActivity(_dockerActivity, "Container deleted", item.Name, "Succeeded"); return Ok("Container deleted."); }
            var isRunning = action is "start" or "restart" or "unpause";
            var next = item with { State = isRunning ? "running" : action == "pause" ? "paused" : "exited", Status = isRunning ? "Up just now" : action == "pause" ? "Paused" : "Exited (0) just now", CpuPercent = isRunning ? 1.9 : 0 };
            _containers[index] = next;
            AddActivity(_dockerActivity, $"Container {action}", item.Name, "Succeeded");
            return Ok($"{item.Name} {action} completed.");
        }
    }

    public IReadOnlyList<DockerStackSummary> Stacks() { lock (_gate) return _stacks.ToArray(); }
    public MockOperationResult SaveStack(DockerStackUpsertRequest request)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || !request.Compose.Contains("services:", StringComparison.Ordinal)) return Fail("A name and a valid Compose services section are required.");
            var index = _stacks.FindIndex(s => s.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            var stack = new DockerStackSummary(request.Name.Trim(), string.IsNullOrWhiteSpace(request.Source) ? "Compose editor" : request.Source, "draft", 1, DateTimeOffset.UtcNow, request.Compose);
            if (index >= 0) _stacks[index] = stack; else _stacks.Add(stack);
            AddActivity(_dockerActivity, "Stack saved", stack.Name, "Succeeded");
            return Ok("Compose definition saved as a draft.");
        }
    }
    public MockOperationResult StackAction(string name, string action, bool confirmed)
    {
        lock (_gate)
        {
            var index = _stacks.FindIndex(s => s.Name == name);
            if (index < 0) return Fail("Stack not found.");
            if (action == "down" && !confirmed) return Fail("Confirmation is required before stopping a stack.");
            var stack = _stacks[index] with { Status = action is "deploy" or "redeploy" ? "running" : "stopped", UpdatedAt = DateTimeOffset.UtcNow };
            _stacks[index] = stack; AddActivity(_dockerActivity, $"Stack {action}", name, "Succeeded");
            return Ok($"Stack {action} completed.");
        }
    }
    public IReadOnlyList<DockerImageSummary> Images() { lock (_gate) return _images.ToArray(); }
    public MockOperationResult PullImage(DockerImagePullRequest request)
    {
        lock (_gate)
        {
            var reference = NormalizeImageReference(request.ImageReference);
            if (!IsPublicImageReference(reference)) return Fail("Enter a public image reference such as nginx:1.27 or redis:7.");
            var (repository, tag) = SplitImageReference(reference);
            var existing = _images.FindIndex(item => item.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) && item.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) return Ok($"{reference} is already available locally.");
            _images.Add(new($"sha256:{Guid.NewGuid():N}"[..17], repository, tag, "Pulled", "just now", false));
            AddActivity(_dockerActivity, "Public image pulled", reference, "Succeeded");
            return Ok($"Pulled public image {reference}.");
        }
    }
    public DockerPrunePreview PrunePreview() => new(0, 1, 0, "220 MB");
    public MockOperationResult Prune(bool confirmed)
    {
        lock (_gate)
        {
            if (!confirmed) return Fail("Review and confirm the cleanup before continuing.");
            _images.RemoveAll(image => !image.InUse); AddActivity(_dockerActivity, "Image cleanup", "Unused images", "Succeeded"); return Ok("Removed 1 unused image and reclaimed 220 MB.");
        }
    }
    public IReadOnlyList<DockerNetworkSummary> Networks() { lock (_gate) return _networks.Select(network => network with { Containers = _containerConfigurations.Values.Count(config => config.Network.Equals(network.Name, StringComparison.OrdinalIgnoreCase)) }).ToArray(); }
    public MockOperationResult CreateNetwork(DockerNetworkCreateRequest request)
    {
        lock (_gate)
        {
            if (!IsContainerNameValid(request.Name)) return Fail("Network names can use letters, numbers, dots, underscores and hyphens.");
            if (request.Driver is not ("bridge" or "host" or "none")) return Fail("Choose bridge, host, or none as the network driver.");
            if (_networks.Any(network => network.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))) return Fail("A network with this name already exists.");
            _networks.Add(new($"net_{Guid.NewGuid():N}"[..10], request.Name.Trim(), request.Driver, 0));
            AddActivity(_dockerActivity, "Network created", request.Name.Trim(), "Succeeded");
            return Ok($"Network {request.Name.Trim()} was created.");
        }
    }
    public MockOperationResult DeleteNetwork(string id, bool confirmed)
    {
        lock (_gate)
        {
            var network = _networks.SingleOrDefault(item => item.Id == id || item.Name == id);
            if (network is null) return Fail("Network not found.");
            if (!confirmed) return Fail("Confirmation is required before deleting a network.");
            if (network.Name is "bridge" or "host" or "none") return Fail("System networks cannot be deleted.");
            if (_containerConfigurations.Values.Any(config => config.Network.Equals(network.Name, StringComparison.OrdinalIgnoreCase))) return Fail("Disconnect containers from this network before deleting it.");
            _networks.Remove(network); AddActivity(_dockerActivity, "Network deleted", network.Name, "Succeeded"); return Ok("Network deleted.");
        }
    }
    public IReadOnlyList<DockerVolumeSummary> Volumes() { lock (_gate) return _volumes.Select(volume => volume with { Consumers = _containerConfigurations.Values.Count(config => config.Mounts.Contains(volume.Name, StringComparison.OrdinalIgnoreCase)) }).ToArray(); }
    public MockOperationResult CreateVolume(DockerVolumeCreateRequest request)
    {
        lock (_gate)
        {
            if (!IsContainerNameValid(request.Name)) return Fail("Volume names can use letters, numbers, dots, underscores and hyphens.");
            if (!request.Driver.Equals("local", StringComparison.OrdinalIgnoreCase)) return Fail("Only the local volume driver is available in this preview.");
            if (_volumes.Any(volume => volume.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))) return Fail("A volume with this name already exists.");
            _volumes.Add(new(request.Name.Trim(), "local", $"/var/lib/docker/volumes/{request.Name.Trim()}", 0));
            AddActivity(_dockerActivity, "Volume created", request.Name.Trim(), "Succeeded"); return Ok($"Volume {request.Name.Trim()} was created.");
        }
    }
    public MockOperationResult DeleteVolume(string name, bool confirmed)
    {
        lock (_gate)
        {
            var volume = _volumes.SingleOrDefault(item => item.Name == name);
            if (volume is null) return Fail("Volume not found.");
            if (!confirmed) return Fail("Confirmation is required before deleting a volume.");
            if (_containerConfigurations.Values.Any(config => config.Mounts.Contains(volume.Name, StringComparison.OrdinalIgnoreCase))) return Fail("Remove this volume from container parameters before deleting it.");
            _volumes.Remove(volume); AddActivity(_dockerActivity, "Volume deleted", volume.Name, "Succeeded"); return Ok("Volume deleted.");
        }
    }

    public ManagerOverview NginxOverview()
    {
        lock (_gate)
        {
            if (!_installed["Nginx"]) return NotInstalledOverview("Nginx", "Nginx is not installed", "Install Nginx to create sites and manage live configuration.");
            return new("Nginx", "attention", "Nginx needs a service check", "Configuration is available; the mock host simulates a stopped service.", [
            new("Enabled sites", _sites.Count(s => s.Enabled).ToString(), "1 disabled site", "neutral"), new("Certificates linked", _sites.Count(s => !string.IsNullOrWhiteSpace(s.Certificate)).ToString(), "HTTPS bindings", "success"), new("Config versions", "4", "Last changed 42 minutes ago", "neutral"), new("Service", "Offline", "Start after a successful config test", "warning")], _nginxActivity.Take(6).ToArray());
        }
    }
    public IReadOnlyList<NginxSiteSummary> Sites() { lock (_gate) return _sites.ToArray(); }
    public MockOperationResult SaveSite(string? id, NginxSiteUpsertRequest request)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Domains) || !Uri.TryCreate(request.Upstream, UriKind.Absolute, out _)) return Fail("Name, domains and an absolute upstream URL are required.");
            var current = new NginxSiteSummary(id ?? $"site_{Guid.NewGuid():N}"[..11], request.Name.Trim(), request.Domains.Trim(), request.Upstream.Trim(), request.Enabled, request.CertificateId ?? "", DateTimeOffset.UtcNow);
            var index = _sites.FindIndex(site => site.Id == id);
            if (index >= 0) _sites[index] = current; else _sites.Add(current);
            AddActivity(_nginxActivity, index >= 0 ? "Site updated" : "Site created", current.Name, "Succeeded"); return Ok("Site configuration saved. Test and reload to apply it.");
        }
    }
    public MockOperationResult DeleteSite(string id, bool confirmed)
    {
        lock (_gate)
        {
            if (!confirmed) return Fail("Confirmation is required before deleting a site.");
            var site = _sites.SingleOrDefault(s => s.Id == id); if (site is null) return Fail("Site not found."); _sites.Remove(site); AddActivity(_nginxActivity, "Site deleted", site.Name, "Succeeded"); return Ok("Site deleted.");
        }
    }
    public NginxTestResult TestNginx() => new(true, ["syntax is ok", "test is successful", "2 server blocks discovered"], DateTimeOffset.UtcNow);
    public MockOperationResult ReloadNginx(bool confirmed) { if (!confirmed) return Fail("A successful test and confirmation are required before reload."); lock (_gate) { AddActivity(_nginxActivity, "Service reloaded", "Nginx", "Succeeded"); return Ok("Nginx configuration reloaded."); } }
    public IReadOnlyList<NginxConfigSnapshot> Configs() => [new("v4", DateTimeOffset.UtcNow.AddMinutes(-42), "design-user", "Proxy settings updated", "server {\n  listen 443 ssl;\n  server_name remoteos.local;\n  proxy_pass http://remoteos-web:80;\n}"), new("v3", DateTimeOffset.UtcNow.AddDays(-1), "design-user", "Certificate binding updated", "server { listen 443 ssl; }")];
    public IReadOnlyList<NginxLogEntry> NginxLogs() => [new(DateTimeOffset.UtcNow.AddMinutes(-2), "info", "RemoteOS portal", "GET /health", 200), new(DateTimeOffset.UtcNow.AddMinutes(-9), "warning", "Example API", "upstream connection refused", 502), new(DateTimeOffset.UtcNow.AddMinutes(-42), "info", "system", "configuration test successful")];

    public ManagerOverview CertificateOverview()
    {
        lock (_gate)
        {
            if (!_installed["Certificates"]) return NotInstalledOverview("Certificates", "Certificate service is not installed", "Install an ACME client to issue and manage certificates.");
            return new("Certificates", "attention", "One certificate needs attention", "Certificate issuance and renewal are mocked; private keys never enter this service.", [new("Valid", _certificates.Count(c => c.Status == "Valid").ToString(), "Automatically renewing", "success"), new("Expiring soon", _certificates.Count(c => c.Status == "Expiring soon").ToString(), "Renew within 12 days", "warning"), new("ACME accounts", "1", "Let's Encrypt production", "neutral"), new("DNS providers", "1", "Credential reference configured", "neutral")], _certificateActivity.Take(6).ToArray());
        }
    }
    public IReadOnlyList<CertificateSummary> Certificates() { lock (_gate) return _certificates.ToArray(); }
    public MockOperationResult IssueCertificate(CertificateIssueRequest request)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(request.PrimaryDomain) || request.Validation is not ("HTTP-01" or "DNS-01")) return Fail("A primary domain and HTTP-01 or DNS-01 validation are required.");
            var domains = new[] { request.PrimaryDomain.Trim() }.Concat(request.AlternativeNames.Where(name => !string.IsNullOrWhiteSpace(name))).ToArray();
            var certificate = new CertificateSummary($"cert_{Guid.NewGuid():N}"[..11], string.Join(", ", domains), "Let's Encrypt", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90)), "Pending validation", request.AutoRenew, request.Validation);
            _certificates.Add(certificate); AddActivity(_certificateActivity, "Certificate issuance requested", certificate.Domains, "Queued"); return Ok("Certificate request queued for validation.");
        }
    }
    public MockOperationResult CertificateAction(string id, string action, bool force)
    {
        lock (_gate)
        {
            var index = _certificates.FindIndex(c => c.Id == id); if (index < 0) return Fail("Certificate not found."); var certificate = _certificates[index];
            if (action == "revoke" && !force) return Fail("Revocation requires an explicit confirmation.");
            if (action == "revoke") { _certificates[index] = certificate with { Status = "Revoked", AutoRenew = false }; }
            else if (action == "renew") { _certificates[index] = certificate with { Status = "Valid", ExpiresOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90)) }; }
            else return Fail("Unsupported certificate action.");
            AddActivity(_certificateActivity, $"Certificate {action}", certificate.Domains, "Succeeded"); return Ok($"Certificate {action} completed.");
        }
    }
    public IReadOnlyList<AcmeAccountSummary> AcmeAccounts() => [new("acme_01", "ops@remoteos.local", "https://acme-v02.api.letsencrypt.org/directory", "Active", DateTimeOffset.UtcNow.AddMonths(-3))];
    public IReadOnlyList<DnsProviderSummary> DnsProviders() => [new("dns_01", "Cloudflare", "secret://remoteos/dns/cloudflare", true)];
    public CertificateRenewalPolicy RenewalPolicy() => new(30, true, "02:00–04:00 UTC");

    private static bool IsContainerNameValid(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 63 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    private static bool IsPublicImageReference(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && !value.Contains("://", StringComparison.Ordinal) && !value.Contains('@') && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or ':');
    private static string NormalizeImageReference(string reference)
    {
        var trimmed = reference.Trim();
        var slash = trimmed.LastIndexOf('/');
        return trimmed.LastIndexOf(':') > slash ? trimmed : $"{trimmed}:latest";
    }
    private static (string Repository, string Tag) SplitImageReference(string reference)
    {
        var separator = reference.LastIndexOf(':');
        return (reference[..separator], reference[(separator + 1)..]);
    }
    private static string ImageReference(DockerImageSummary image) => $"{image.Repository}:{image.Tag}";
    private void MarkImageInUse(string reference)
    {
        var index = _images.FindIndex(image => ImageReference(image).Equals(reference, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _images[index] = _images[index] with { InUse = true };
    }
    private void UpdateImageUsage()
    {
        for (var index = 0; index < _images.Count; index++)
        {
            var image = _images[index];
            _images[index] = image with { InUse = _containers.Any(container => container.Image.Equals(ImageReference(image), StringComparison.OrdinalIgnoreCase)) };
        }
    }

    private ManagerStatus Status(string name, string version, string message, IReadOnlyList<string> steps) => new(name, _installed[name], version, message, steps);
    private static ManagerOverview NotInstalledOverview(string manager, string headline, string detail) => new(manager, "unavailable", headline, detail, [new("Installation", "Not installed", "Use the simulation control to preview the installed state.", "warning")], []);
    private static MockOperationResult Ok(string message) => new(true, message, DateTimeOffset.UtcNow, $"op_{Guid.NewGuid():N}"[..11]);
    private static MockOperationResult Fail(string message) => new(false, message, DateTimeOffset.UtcNow);
    private static void AddActivity(List<ActivityItem> activities, string action, string target, string result) => activities.Insert(0, new(DateTimeOffset.UtcNow, action, target, result));

    private sealed record DockerContainerConfiguration(string Name, string Command, string Ports, string Environment, string Mounts, string Network, string RestartPolicy);
}
