using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOS.Protocol.Certificates;
using RemoteOS.Protocol.WebServers;
using Server.Certificate;
using Server.Storage.Sqlite;
using Server.WebServer;

var root = Path.Combine(Path.GetTempPath(), $"remoteos-server-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    await VerifyCertificateStoreAndSniAsync(root);
    VerifyCertificateApiRoutes();
    await VerifyRenewalRetryAsync(root);
    await VerifyHostGlobalMigrationAsync(root);
    await VerifyDeploymentAndNginxSnapshotsAsync(root);
    await VerifyWebServerProviderRoutingAsync();
    await VerifyOperationIdempotencyAsync(root);
    Console.WriteLine("RemoteOS.Server backend verification passed.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static async Task VerifyCertificateStoreAndSniAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var options = new CertificateOptions { StorageRoot = Path.Combine(root, "certificates"), VersionRetentionCount = 2 };
    var metadata = new CertificateMetadataRepository(environment, configuration);
    var store = new FileCertificateStore(environment, options, metadata);
    var certificateId = Guid.NewGuid();
    var first = CreateMaterial(certificateId, "one.example.test");
    var second = CreateMaterial(certificateId, "one.example.test");
    var third = CreateMaterial(certificateId, "one.example.test");
    await store.SaveAsync(first, CancellationToken.None);
    await store.SaveAsync(second, CancellationToken.None);
    await store.SaveAsync(third, CancellationToken.None);
    var stored = await store.GetAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Certificate metadata was not saved.");
    Assert(stored.Version.Length == 32, "Certificate version was not generated.");
    var versions = Directory.EnumerateDirectories(Path.Combine(options.StorageRoot!, certificateId.ToString("D"), "versions")).ToArray();
    Assert(versions.Length == 2, "Certificate version retention did not prune old material.");
    if (!OperatingSystem.IsWindows())
    {
        var certificateRoot = Path.Combine(options.StorageRoot!, certificateId.ToString("D"));
        Assert(File.GetUnixFileMode(certificateRoot) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Certificate root permissions are not private.");
        Assert(File.GetUnixFileMode(Path.Combine(certificateRoot, "versions", stored.Version, "private.key")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Private-key permissions are not private.");
    }
    using var loaded = await store.LoadCurrentAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Current certificate did not load.");
    Assert(loaded.HasPrivateKey, "Stored certificate lost its private key.");
    var nginxPaths = await store.GetNginxPathsAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Nginx certificate paths were not created.");
    Assert(File.Exists(nginxPaths.FullChainPath) && File.Exists(nginxPaths.PrivateKeyPath), "Stable Nginx certificate material is missing.");

    var registry = new KestrelCertificateRegistry();
    using var firstSni = CreateX509("one.example.test");
    using var secondSni = CreateX509("two.example.test");
    Assert(registry.Activate(Guid.NewGuid(), firstSni, ["one.example.test"]), "First SNI activation failed.");
    var secondId = Guid.NewGuid();
    Assert(registry.Activate(secondId, secondSni, ["two.example.test"]), "Second SNI activation failed.");
    Assert(registry.Select("one.example.test") == firstSni, "First SNI binding was lost.");
    Assert(registry.Select("two.example.test") == secondSni, "Second SNI binding was not selected.");
    Assert(registry.Deactivate(secondId), "Second SNI binding did not deactivate.");
    Assert(registry.Select("one.example.test") == firstSni, "Unrelated SNI binding changed during deactivation.");
}

static void VerifyCertificateApiRoutes()
{
    Assert(CertificateApiRoutes.Certificates == "/api/v1/certificates", "Certificate collection route changed unexpectedly.");
    Assert(CertificateApiRoutes.Request == CertificateApiRoutes.Certificates, "Certificate request route must use the collection endpoint.");
    Assert(CertificateApiRoutes.CollectionPattern.Length == 0, "Certificate collection pattern must remain group-relative.");
}

static async Task VerifyRenewalRetryAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var repository = new CertificateRenewalAttemptRepository(environment, configuration,
        new CertificateOptions { RenewalRetryMaxAttempts = 2, RenewalRetryBaseDelayMinutes = 1 });
    var certificateId = Guid.NewGuid();
    var failed = new CertificateOperationDto(Guid.NewGuid(), certificateId, "renew", CertificateOperationState.Failed, "failed", "certificate.acme_request_failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await repository.RecordAsync(failed, CancellationToken.None);
    var schedule = await repository.GetScheduleAsync(certificateId, CancellationToken.None);
    Assert(schedule.ConsecutiveFailures == 1 && schedule.RetryAfter > DateTimeOffset.UtcNow && !schedule.Exhausted, "Renewal retry backoff was not persisted.");
    var succeeded = new CertificateOperationDto(Guid.NewGuid(), certificateId, "renew", CertificateOperationState.Succeeded, "succeeded", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await repository.RecordAsync(succeeded, CancellationToken.None);
    Assert((await repository.GetScheduleAsync(certificateId, CancellationToken.None)).ConsecutiveFailures == 0, "A successful renewal did not reset retry state.");
}

static async Task VerifyHostGlobalMigrationAsync(string root)
{
    var databasePath = Path.Combine(root, "host-global.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT MAX(version) FROM remoteos_host_schema_migrations;";
    Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 6, "HostGlobal migrations did not reach the expected version.");
}

static async Task VerifyDeploymentAndNginxSnapshotsAsync(string root)
{
    var databasePath = Path.Combine(root, "deployment-and-snapshots.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Storage:Provider"] = "sqlite",
        ["Storage:DatabasePath"] = databasePath
    }).Build();
    var certificateId = Guid.NewGuid();
    var certificate = new StoredCertificate(certificateId, "a".PadLeft(32, 'a'), "one.example.test", ["one.example.test"],
        CertificateChallengeType.WebRootHttp01, CertificateKeyAlgorithm.EcdsaP256, null, null, null, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(7), CertificateStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "ops@example.test", null, null, null, null);
    var deployments = new CertificateDeploymentRepository(environment, configuration);
    await deployments.RecordKestrelAsync(certificate, false, "certificate.kestrel_activation_failed", CancellationToken.None);
    Assert((await deployments.ListKestrelAsync(CancellationToken.None)).Count == 0, "A failed Kestrel deployment became restartable.");
    await deployments.RecordKestrelAsync(certificate, true, null, CancellationToken.None);
    Assert((await deployments.ListKestrelAsync(CancellationToken.None)).Single().CurrentVersion == certificate.Version, "A successful Kestrel deployment was not persisted.");

    var configPath = Path.Combine(root, "nginx.conf");
    Directory.CreateDirectory(Path.Combine(root, "conf.d"));
    await File.WriteAllTextAsync(configPath, "events {}\nhttp {\n  include conf.d/*.conf;\n}\n");
    var instance = new WebServerDto("nginx-test", "nginx", WebServerType.Nginx, WebServerManagementMode.External, "/usr/sbin/nginx", configPath,
        "test", DateTimeOffset.UtcNow, new WebServerCapabilities(true, true, false, false));
    var webServers = new WebServerMetadataRepository(environment, configuration);
    await webServers.UpsertInstanceAsync(instance, CancellationToken.None);
    var snapshot = await webServers.CreateSnapshotAsync(instance, CancellationToken.None) ?? throw new InvalidOperationException("Nginx snapshot was not created.");
    Assert(await webServers.IsSnapshotCurrentAsync(configPath, snapshot, CancellationToken.None), "Fresh Nginx snapshot was incorrectly stale.");
    await File.AppendAllTextAsync(configPath, "# external change\n");
    Assert(!await webServers.IsSnapshotCurrentAsync(configPath, snapshot, CancellationToken.None), "Nginx external modification was not detected.");

    var resolver = typeof(NginxWebServerManager).GetMethod("FindOwnedIncludeDirectory", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx include resolver was not found.");
    var resolved = (string?)resolver.Invoke(null, [configPath]);
    Assert(string.Equals(resolved, Path.Combine(root, "conf.d"), StringComparison.Ordinal), "Nginx http-context include was not found.");
    var outsideHttp = Path.Combine(root, "outside-http.conf");
    await File.WriteAllTextAsync(outsideHttp, "include conf.d/*.conf;\nevents {}\nhttp {}\n");
    Assert(resolver.Invoke(null, [outsideHttp]) is null, "Nginx include outside http context was accepted.");

    var managedRoot = Path.Combine(root, "managed-nginx");
    var managedConfiguration = Path.Combine(managedRoot, "conf", "nginx.conf");
    var managedConfD = Path.Combine(managedRoot, "conf", "conf.d");
    Directory.CreateDirectory(managedConfD);
    await File.WriteAllTextAsync(managedConfiguration, "events {}\nhttp { include conf.d/*.conf; }\n");
    var managedInstance = new WebServerDto("managed-test", "nginx", WebServerType.Nginx, WebServerManagementMode.Managed,
        Path.Combine(managedRoot, "sbin", "nginx"), managedConfiguration, "test", DateTimeOffset.UtcNow,
        new WebServerCapabilities(true, true, false, false));
    var ensureAnchor = typeof(NginxWebServerManager).GetMethod("EnsureSiteIncludeAnchorAsync", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx site anchor initializer was not found.");
    var anchorResult = (Task<string?>)ensureAnchor.Invoke(null, [managedInstance, CancellationToken.None])!;
    Assert(await anchorResult is null, "A managed Nginx instance did not create its first site anchor.");
    Assert(File.Exists(Path.Combine(managedConfD, "remoteos.conf")), "The managed Nginx site anchor was not created.");

    var validServerName = typeof(NginxWebServerManager).GetMethod("IsValidServerName", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx server-name validator was not found.");
    Assert((bool)validServerName.Invoke(null, ["192.0.2.10"])!, "IPv4 addresses were rejected as Nginx server names.");
    Assert((bool)validServerName.Invoke(null, ["2001:db8::10"])!, "IPv6 addresses were rejected as Nginx server names.");
    Assert(!(bool)validServerName.Invoke(null, ["example.com; return 200"])!, "Unsafe Nginx server name was accepted.");

    var multiPortSite = new WebServerSiteDto("multi-port", "nginx-test", "multi-port", WebServerSiteKind.Static,
        ["app.example.test", "admin.example.test"], 5000, null, "/srv/remoteos-sites/multi-port", null, false, DateTimeOffset.UtcNow,
        [new WebServerSiteBindingDto("app.example.test", 5000), new WebServerSiteBindingDto("admin.example.test", 6000)]);
    Assert(multiPortSite.DomainsDisplay == "app.example.test:5000, admin.example.test:6000", "Multi-port bindings were not formatted for the site table.");
    var renderSite = typeof(NginxWebServerManager).GetMethod("RenderSiteConfiguration", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx site renderer was not found.");
    var rendered = (string)renderSite.Invoke(null, [multiPortSite, null])!;
    Assert(rendered.Split("server {", StringSplitOptions.None).Length == 2 && rendered.Contains("listen 5000;") && rendered.Contains("listen 6000;")
        && rendered.Contains("server_name app.example.test admin.example.test;"), "A multi-port site was not rendered as one Nginx server with all listeners and names.");
    var proxySite = multiPortSite with { Id = "proxy-site", Kind = WebServerSiteKind.ReverseProxy, RootPath = null, Upstream = "http://127.0.0.1:5090" };
    var renderedProxy = (string)renderSite.Invoke(null, [proxySite, null])!;
    Assert(renderedProxy.Contains("proxy_http_version 1.1;")
        && renderedProxy.Contains("proxy_set_header Upgrade $http_upgrade;")
        && renderedProxy.Contains("proxy_set_header Connection \"upgrade\";"), "A reverse-proxy site did not preserve WebSocket upgrades for SignalR.");

    var findRoutingConflict = typeof(NginxWebServerManager).GetMethod("FindRoutingConflict", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx site conflict detector was not found.");
    var existingSite = new WebServerSiteDto("existing", "nginx-test", "existing", WebServerSiteKind.Static,
        ["app.example.test"], 5000, null, "/srv/remoteos-sites/existing", null, false, DateTimeOffset.UtcNow,
        [new WebServerSiteBindingDto("app.example.test", 5000)]);
    var conflictingSite = new WebServerSiteDto("new-site", "nginx-test", "new-site", WebServerSiteKind.Static,
        ["app.example.test"], 5000, null, "/srv/remoteos-sites/new-site", null, false, DateTimeOffset.UtcNow,
        [new WebServerSiteBindingDto("app.example.test", 5000)]);
    Assert(findRoutingConflict.Invoke(null, [new[] { existingSite }, conflictingSite]) is not null, "Duplicate domain and port bindings were not rejected.");
    var tlsSite = existingSite with { Id = "tls-site", HttpsEnabled = true };
    var port443Site = conflictingSite with { Id = "port-443-site", Bindings = [new WebServerSiteBindingDto("app.example.test", 443)] };
    Assert(findRoutingConflict.Invoke(null, [new[] { tlsSite }, port443Site]) is not null, "The implicit HTTPS listener was not checked for conflicts.");
    var crossProductSite = conflictingSite with { Id = "cross-product-site", Bindings = [new WebServerSiteBindingDto("other.example.test", 5000), new WebServerSiteBindingDto("app.example.test", 6000)] };
    Assert(findRoutingConflict.Invoke(null, [new[] { existingSite }, crossProductSite]) is not null, "All site names were not checked against every configured listener.");
}

static async Task VerifyOperationIdempotencyAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var options = new CertificateOptions { StorageRoot = Path.Combine(root, "operation-certificates") };
    var certificates = new FileCertificateStore(environment, options, new CertificateMetadataRepository(environment, configuration));
    var retries = new CertificateRenewalAttemptRepository(environment, configuration, options);
    var journal = new HostOperationJournal(environment, configuration);
    var certificateOperations = new CertificateOperationStore(environment, journal, certificates, retries, NullLogger<CertificateOperationStore>.Instance);
    var certificateId = Guid.NewGuid();
    var first = await certificateOperations.StartAsync("same-key", certificateId, "issue", "test", _ => Task.FromResult(""), CancellationToken.None);
    var duplicate = await certificateOperations.StartAsync("same-key", certificateId, "issue", "test", _ => Task.FromResult("certificate.should_not_run"), CancellationToken.None);
    Assert(first.OperationId == duplicate.OperationId, "Certificate idempotency key created duplicate work.");
    Assert((await WaitForCertificateOperationAsync(certificateOperations, first.OperationId)).State == CertificateOperationState.Succeeded, "Certificate operation did not complete.");

    var webOperations = new WebServerOperationStore(environment, journal);
    var webFirst = await webOperations.StartAsync("same-key", "nginx-test", "reload", "test", _ => Task.FromResult(WebServerOperationResult.Success), CancellationToken.None);
    var webDuplicate = await webOperations.StartAsync("same-key", "nginx-test", "reload", "test", _ => Task.FromResult(new WebServerOperationResult("webserver.should_not_run")), CancellationToken.None);
    Assert(webFirst.OperationId == webDuplicate.OperationId, "WebServer idempotency key created duplicate work.");
    Assert((await WaitForWebOperationAsync(webOperations, webFirst.OperationId)).State == WebServerOperationState.Succeeded, "WebServer operation did not complete.");
}

static async Task VerifyWebServerProviderRoutingAsync()
{
    var provider = new FakeWebServerProvider();
    IWebServerManager manager = new WebServerManager([provider]);
    var discovered = await manager.DiscoverAsync(CancellationToken.None);
    Assert(discovered.Count == 1 && discovered[0].ProviderId == provider.ProviderId, "Web Server Manager did not aggregate provider discovery.");
    var status = await manager.GetStatusAsync(provider.Instance.Id, CancellationToken.None);
    Assert(status?.RuntimeState == WebServerRuntimeState.Running, "Web Server Manager did not route the instance to its provider.");
    Assert(await manager.GetStatusAsync("unknown", CancellationToken.None) is null, "Web Server Manager routed an unknown instance.");
}

static async Task<CertificateOperationDto> WaitForCertificateOperationAsync(CertificateOperationStore operations, Guid id)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var operation = await operations.GetAsync(id, CancellationToken.None) ?? throw new InvalidOperationException("Certificate operation disappeared.");
        if (operation.State is CertificateOperationState.Succeeded or CertificateOperationState.Failed or CertificateOperationState.Cancelled) return operation;
        await Task.Delay(10);
    }
    throw new TimeoutException("Certificate operation did not complete.");
}

static async Task<WebServerOperationDto> WaitForWebOperationAsync(WebServerOperationStore operations, Guid id)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var operation = await operations.GetAsync(id, CancellationToken.None) ?? throw new InvalidOperationException("WebServer operation disappeared.");
        if (operation.State is WebServerOperationState.Succeeded or WebServerOperationState.Failed or WebServerOperationState.Cancelled) return operation;
        await Task.Delay(10);
    }
    throw new TimeoutException("WebServer operation did not complete.");
}

static CertificateMaterial CreateMaterial(Guid id, string domain)
{
    using var certificate = CreateX509(domain);
    return new CertificateMaterial(id, [domain], CertificateChallengeType.WebRootHttp01, CertificateKeyAlgorithm.EcdsaP256,
        "ops@example.test", certificate.ExportCertificatePem(), certificate.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
}

static X509Certificate2 CreateX509(string domain)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7));
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "RemoteOS.Server.Tests";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
}

sealed class FakeWebServerProvider : IWebServerProvider
{
    public string ProviderId => "fake";
    public WebServerDto Instance { get; } = new("fake-instance", "fake", WebServerType.Nginx, WebServerManagementMode.External,
        "/fake/nginx", null, "test", DateTimeOffset.UtcNow, new WebServerCapabilities(true, true, false, false));

    public Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebServerDto>>([Instance]);
    public Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<WebServerStatusDto?>(instanceId == Instance.Id ? new WebServerStatusDto(instanceId, WebServerRuntimeState.Running) : null);
    public Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<WebServerConfigTestResultDto?>(null);
    public Task<WebServerOperationDto?> InstallManagedAsync(string idempotencyKey, InstallManagedWebServerRequest request, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<WebServerInstallPackageDto?> UploadManagedPackageAsync(string fileName, Stream content, CancellationToken cancellationToken) => Task.FromResult<WebServerInstallPackageDto?>(null);
    public Task<WebServerInstallCatalogDto?> GetManagedInstallCatalogAsync(CancellationToken cancellationToken) => Task.FromResult<WebServerInstallCatalogDto?>(null);
    public Task<WebServerOperationDto?> IntegrateAsync(string instanceId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<WebServerOperationDto?> UninstallManagedAsync(string instanceId, string idempotencyKey, UninstallManagedWebServerRequest request, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebServerSiteDto>?>([]);
    public Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, CancellationToken cancellationToken) => Task.FromResult<WebServerSiteDto?>(null);
    public Task<bool?> DeleteSiteAsync(string instanceId, string siteId, CancellationToken cancellationToken) => Task.FromResult<bool?>(false);
}
