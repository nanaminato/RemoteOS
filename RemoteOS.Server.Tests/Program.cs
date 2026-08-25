using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.Protocol.Certificates;
using RemoteOS.Protocol.Desktop;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Protocol.WebServers;
using RemoteOS.Protocol.Tunnels;
using Server.Certificate;
using Server.Domain;
using Server.Storage.Sqlite;
using Server.SystemPerformance;
using Server.Tunnels;
using Server.Runtimes;
using Server.Secrets;
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
    VerifyTunnelProtocolContract();
    VerifyFrpTomlSafety();
    await VerifyFrpRuntimeInstallAndRollbackAsync(root);
    await VerifyTunnelSecretLifecycleAsync(root);
    VerifyWorkspacePreferencesJsonContract();
    VerifyThemePaletteContract();
    await VerifyTrackedWorkspaceWallpaperUpdateAsync(root);
    await VerifyPerformanceSamplerAsync();
    Console.WriteLine("RemoteOS.Server backend verification passed.");
}

finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static void VerifyWorkspacePreferencesJsonContract()
{
    var preferences = new WorkspacePreferencesDto(
        WorkspacePreferencesDto.CustomWallpaperPrefix + Guid.NewGuid().ToString("N"),
        ThemeKind.Dark,
        WorkspacePreferencesDto.TimeFormat12H,
        "M/d/yyyy",
        "en-US",
        "en-US",
        [new DefaultAppMappingDto("https", "remoteos.browser")]);

    var json = JsonSerializer.Serialize(preferences, RemoteOS.Protocol.Common.RemoteOsJsonOptions.Default);
    var deserialized = JsonSerializer.Deserialize<WorkspacePreferencesDto>(json, RemoteOS.Protocol.Common.RemoteOsJsonOptions.Default)
        ?? throw new InvalidOperationException("Workspace preferences JSON did not deserialize.");

    Assert(deserialized.WallpaperKey == preferences.WallpaperKey, "Wallpaper key changed during JSON deserialization.");
    Assert(deserialized.DefaultApps.SequenceEqual(preferences.DefaultApps), "Default app mappings changed during JSON deserialization.");
}

static void VerifyThemePaletteContract()
{
    var preferences = new ThemePreferencesDto
    {
        PaletteId = "custom:paired",
        CustomPalettes =
        [
            new ThemePaletteDto
            {
                Id = "paired", Name = "Paired palette",
                LightColors = new(StringComparer.OrdinalIgnoreCase) { ["Accent"] = "#0078D4" },
                DarkColors = new(StringComparer.OrdinalIgnoreCase) { ["Accent"] = "#89B4FA" },
            },
        ],
    };
    var light = ThemePaletteDefaults.Resolve(preferences, dark: false);
    var dark = ThemePaletteDefaults.Resolve(preferences, dark: true);
    Assert(light["Accent"] == "#0078D4" && dark["Accent"] == "#89B4FA", "Paired custom palette did not retain mode-specific accents.");
    Assert(ThemePaletteValidator.TryValidate(light, out _) && ThemePaletteValidator.TryValidate(dark, out _), "Built palette did not meet contrast requirements.");
    Assert(light["TextOnAccent"] == "#000000" && dark["TextOnAccent"] == "#000000", "Accent foreground was not chosen for contrast.");

    var exported = JsonSerializer.Serialize(preferences.CustomPalettes.Single(), RemoteOS.Protocol.Common.RemoteOsJsonOptions.Default);
    var imported = JsonSerializer.Deserialize<ThemePaletteDto>(exported, RemoteOS.Protocol.Common.RemoteOsJsonOptions.Default);
    Assert(ThemePaletteImport.TryNormalize(imported, ["paired"], accentOverride: null, out var normalized, out var importError),
        $"Exported custom palette could not be imported: {importError}.");
    Assert(normalized!.Id == "paired-2" && normalized.LightColors!["Accent"] == "#0078D4" && normalized.DarkColors!["Accent"] == "#89B4FA",
        "Imported palette was not normalised to a distinct paired palette.");

    imported!.LightColors!["UntrustedToken"] = "#FFFFFF";
    Assert(!ThemePaletteImport.TryNormalize(imported, [], accentOverride: null, out _, out var rejectedError)
           && rejectedError == ThemePaletteImportError.InvalidFormat,
        "Palette import accepted a token outside the stable colour contract.");
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

static void VerifyTunnelProtocolContract()
{
    Assert(TunnelApiRoutes.Tunnels == "/api/v1/tunnels", "Tunnel API base route changed unexpectedly.");
    var profile = new TunnelServerProfileDto(Guid.NewGuid(), "edge", "frps.example.test", 7000,
        TunnelAuthKind.Token, true, TunnelTlsMode.Default, TunnelRuntimeMode.External, "/opt/frp/frpc", 3,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var json = JsonSerializer.Serialize(profile, RemoteOS.Protocol.Common.RemoteOsJsonOptions.Default);
    Assert(!json.Contains("\"token\":", StringComparison.OrdinalIgnoreCase) && !json.Contains("secret", StringComparison.OrdinalIgnoreCase),
        "Safe tunnel profile DTO must not serialize credential material.");
    Assert(json.Contains("tokenConfigured", StringComparison.Ordinal), "Safe tunnel profile DTO lost configured-state indicator.");
    var definition = new TunnelDefinitionDto(Guid.NewGuid(), profile.Id, "ssh", "frp", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null, true, false, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var roundTrip = JsonSerializer.Deserialize<TunnelDefinitionDto>(JsonSerializer.Serialize(definition, RemoteOS.Protocol.Common.RemoteOsJsonOptions.Default), RemoteOS.Protocol.Common.RemoteOsJsonOptions.Default);
    Assert(roundTrip?.Protocol == TunnelProtocol.Tcp && roundTrip.RemotePort == 6000, "Tunnel desired-state DTO JSON contract changed.");
}

static void VerifyFrpTomlSafety()
{
    Assert(TunnelValidation.ValidateDefinition("bad\nname", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null) == "tunnel.definition_invalid",
        "Tunnel name validation allowed TOML control characters.");
    Assert(TunnelValidation.ValidateDefinition("http", TunnelProtocol.Http, "::1", 8080, null, "app.example.test") is null,
        "IPv6 loopback HTTP tunnel was rejected.");
    var profile = new TunnelServerProfileDto(Guid.NewGuid(), "edge", "frps.example.test", 7000, TunnelAuthKind.Token, true,
        TunnelTlsMode.Force, TunnelRuntimeMode.Managed, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var tunnel = new TunnelDefinitionDto(Guid.NewGuid(), profile.Id, "api", "frp", TunnelProtocol.Https, "::1", 8443,
        null, "app.example.test", true, true, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var toml = FrpTomlGenerator.Generate(profile, [tunnel], "token-with-\\-and-\"quote");
    Assert(toml.Contains("token = \"token-with-\\\\-and-\\\"quote\"", StringComparison.Ordinal), "FRP TOML token escaping changed.");
    Assert(toml.Contains("customDomains = [\"app.example.test\"]", StringComparison.Ordinal) && toml.Contains("[proxies.transport]", StringComparison.Ordinal),
        "FRP TOML generator lost HTTPS domain or transport options.");
}

static async Task VerifyFrpRuntimeInstallAndRollbackAsync(string root)
{
    if (!OperatingSystem.IsLinux()) return;
    var archive = CreateFrpFixtureArchive();
    var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
    var releases = new[]
    {
        new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = digest, ArchiveFormat = "tar.gz" },
        new FrpRuntimeRelease { Version = "v0.71.1", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.1/frp_0.71.1_linux_amd64.tar.gz", Sha256 = digest, ArchiveFormat = "tar.gz" },
    };
    var runtimeRoot = Path.Combine(root, "frp-runtime"); Directory.CreateDirectory(runtimeRoot);
    var env = new TestHostEnvironment(runtimeRoot);
    var manager = new FrpRuntimeManager(env, new FixtureHttpClientFactory(archive), Options.Create(new FrpRuntimeOptions { Releases = releases }));
    var first = await manager.InstallManagedFrpcAsync("v0.71.0", CancellationToken.None);
    Assert(first.Succeeded, "Verified FRP fixture did not install.");
    Assert(manager.GetManagedFrpcInstallationStatus().State == TunnelRuntimeInstallationState.Succeeded
        && manager.GetManagedFrpcInstallationStatus().Progress == 100,
        "Successful runtime installation did not publish completion status.");
    await VerifyFrpApplyLifecycleAsync(root, env, manager);
    var serverArchive = Path.Combine(root, "frp-server-package.tar.gz");
    await File.WriteAllBytesAsync(serverArchive, archive);
    var second = await manager.InstallManagedFrpcFromArchiveAsync("v0.71.1", serverArchive, CancellationToken.None);
    Assert(second.Succeeded, "Verified FRP fixture selected from the server did not install.");
    var active = await manager.GetManagedFrpcStatusAsync(CancellationToken.None);
    Assert(active.Version == "v0.71.1" && active.PreviousVersion == "v0.71.0" && active.IntegrityVerified, "Runtime activation did not preserve previous version state.");
    var rolledBack = await manager.RollbackManagedFrpcAsync(CancellationToken.None);
    Assert(rolledBack.Succeeded && (await manager.GetManagedFrpcStatusAsync(CancellationToken.None)).Version == "v0.71.0", "Runtime rollback did not restore verified previous version.");

    var invalidChecksum = await manager.InstallManagedFrpcAsync("v0.99.0", CancellationToken.None);
    Assert(!invalidChecksum.Succeeded && invalidChecksum.ProblemCode == "tunnel.runtime_release_not_configured", "Unconfigured runtime version was accepted.");

    var badChecksumRoot = Path.Combine(root, "frp-runtime-bad-checksum"); Directory.CreateDirectory(badChecksumRoot);
    var badChecksumManager = new FrpRuntimeManager(new TestHostEnvironment(badChecksumRoot), new FixtureHttpClientFactory(archive), Options.Create(new FrpRuntimeOptions
    {
        Releases = [new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = new string('0', 64), ArchiveFormat = "tar.gz" }],
    }));
    var badChecksum = await badChecksumManager.InstallManagedFrpcAsync("v0.71.0", CancellationToken.None);
    Assert(!badChecksum.Succeeded && badChecksum.ProblemCode == "tunnel.runtime_checksum_failed", "Wrong checksum was accepted.");
    Assert(badChecksumManager.GetManagedFrpcInstallationStatus().State == TunnelRuntimeInstallationState.Failed
        && badChecksumManager.GetManagedFrpcInstallationStatus().ProblemCode == "tunnel.runtime_checksum_failed",
        "Failed runtime installation did not publish failure status.");
    Assert((await badChecksumManager.GetManagedFrpcStatusAsync(CancellationToken.None)).State == TunnelRuntimeState.NotInstalled, "Checksum failure changed the active runtime.");

    var maliciousArchive = CreateMaliciousFrpFixtureArchive();
    var maliciousDigest = Convert.ToHexString(SHA256.HashData(maliciousArchive)).ToLowerInvariant();
    var maliciousRoot = Path.Combine(root, "frp-runtime-malicious"); Directory.CreateDirectory(maliciousRoot);
    var maliciousManager = new FrpRuntimeManager(new TestHostEnvironment(maliciousRoot), new FixtureHttpClientFactory(maliciousArchive), Options.Create(new FrpRuntimeOptions
    {
        Releases = [new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = maliciousDigest, ArchiveFormat = "tar.gz" }],
    }));
    var malicious = await maliciousManager.InstallManagedFrpcAsync("v0.71.0", CancellationToken.None);
    Assert(!malicious.Succeeded && malicious.ProblemCode == "tunnel.runtime_archive_unexpected_entry", "Unexpected archive content was accepted.");
    Assert((await maliciousManager.GetManagedFrpcStatusAsync(CancellationToken.None)).State == TunnelRuntimeState.NotInstalled, "Rejected archive changed the active runtime.");
}

static byte[] CreateFrpFixtureArchive()
{
    using var target = new MemoryStream();
    using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        Write("frp/frpc", "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo frpc-fixture; exit 0; fi\nif [ \"$1\" = \"verify\" ]; then exit 0; fi\nif [ \"$1\" = \"-c\" ]; then echo 'login to server success'; sleep 30; fi\n");
        Write("frp/frps", "#!/bin/sh\necho frps-fixture\n");
        void Write(string name, string content) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
    }
    return target.ToArray();
}

static byte[] CreateMaliciousFrpFixtureArchive()
{
    using var target = new MemoryStream();
    using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/frpc") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho frpc-fixture\n")) });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/frps") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho frps-fixture\n")) });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/unexpected-plugin") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("not allowed")) });
    }
    return target.ToArray();
}

static async Task VerifyFrpApplyLifecycleAsync(string root, IHostEnvironment environment, IRuntimeManager runtime)
{
    var path = Path.Combine(root, "frp-apply-lifecycle.db");
    var services = new ServiceCollection();
    services.AddDbContext<RemoteOsDbContext>(options => options.UseSqlite($"Data Source={path}"));
    services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "frp-apply-keys")));
    services.AddScoped<ISecretStore, DataProtectionSecretStore>();
    services.AddScoped<ITunnelAudit, TunnelAudit>();
    services.AddScoped<ITunnelService, TunnelService>();
    services.AddSingleton<IRuntimeManager>(runtime);
    services.AddSingleton<ITunnelProvider>(provider => new FrpTunnelProvider(provider.GetRequiredService<IServiceScopeFactory>(), environment, provider.GetRequiredService<IRuntimeManager>()));
    await using var container = services.BuildServiceProvider();
    await using (var scope = container.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<RemoteOsDbContext>(); await db.Database.EnsureCreatedAsync();
        var service = scope.ServiceProvider.GetRequiredService<ITunnelService>();
        var profile = await service.UpsertProfileAsync(null, new UpsertTunnelServerProfileRequest("managed", "frps.example.test", 7000, TunnelAuthKind.None, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null), "apply-user", CancellationToken.None);
        await service.UpsertTunnelAsync(null, new UpsertTunnelDefinitionRequest(profile.Id, "ssh", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null, true, false, false), "apply-user", CancellationToken.None);
        var provider = scope.ServiceProvider.GetRequiredService<ITunnelProvider>();
        var applied = await provider.ApplyAsync(profile.Id, "apply-user", CancellationToken.None);
        Assert(applied.Succeeded && applied.State == TunnelConnectionState.Starting, "Managed FRP desired state was not started.");
        var current = await provider.ListAsync("apply-user", CancellationToken.None);
        Assert(current.Single().State is TunnelConnectionState.Starting or TunnelConnectionState.Connected, "Running FRP profile was not reflected as a real runtime state.");
        Assert((await provider.GetLogsAsync(profile.Id, "apply-user", CancellationToken.None))?.All(entry => !entry.Message.Contains("token", StringComparison.OrdinalIgnoreCase)) == true, "Runtime log exposed a token.");
        Assert((await provider.StopAsync(profile.Id, "apply-user", CancellationToken.None)).Succeeded, "Managed FRP process could not be stopped.");
    }
}

static async Task VerifyTunnelSecretLifecycleAsync(string root)
{
    var path = Path.Combine(root, "tunnel-secret-lifecycle.db");
    var dbOptions = new DbContextOptionsBuilder<RemoteOsDbContext>().UseSqlite($"Data Source={path}").Options;
    await using var db = new RemoteOsDbContext(dbOptions); await db.Database.EnsureCreatedAsync();
    var protection = DataProtectionProvider.Create(Path.Combine(root, "data-protection"));
    var secrets = new DataProtectionSecretStore(db, protection);
    var service = new TunnelService(db, secrets, new TunnelAudit(db));
    const string user = "tunnel-test-user";
    var created = await service.UpsertProfileAsync(null, new UpsertTunnelServerProfileRequest("edge", "frps.example.test", 7000,
        TunnelAuthKind.Token, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null), user, CancellationToken.None);
    try
    {
        await service.UpsertTunnelAsync(null, new UpsertTunnelDefinitionRequest(created.Id, "invalid", TunnelProtocol.Http, "127.0.0.1", 8080, 6000, null, true, false, false), user, CancellationToken.None);
        throw new InvalidOperationException("Invalid HTTP tunnel was accepted.");
    }
    catch (TunnelValidationException exception) { Assert(exception.ProblemCode == "tunnel.domain_required", "Invalid tunnel did not return stable problem code."); }
    await service.SetProfileTokenAsync(created.Id, "credential-that-must-not-return", user, CancellationToken.None);
    var safe = await service.GetProfileAsync(created.Id, user, CancellationToken.None) ?? throw new InvalidOperationException("Tunnel profile disappeared.");
    Assert(safe.TokenConfigured && safe.GetType().GetProperties().All(property => !property.Name.Equals("Token", StringComparison.OrdinalIgnoreCase)), "Safe profile projection exposed token material.");
    var updated = await service.UpsertProfileAsync(created.Id, new UpsertTunnelServerProfileRequest("edge", "frps.example.test", 7000,
        TunnelAuthKind.None, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null, safe.Revision), user, CancellationToken.None);
    Assert(!updated.TokenConfigured && !await db.TunnelSecrets.AnyAsync(), "Changing auth away from token left an orphan secret.");
    Assert(await service.DeleteProfileAsync(created.Id, user, CancellationToken.None), "Unused profile could not be deleted.");
}

static async Task VerifyPerformanceSamplerAsync()
{
    if (OperatingSystem.IsLinux())
    {
        var linux = new LinuxPerformanceSource();
        var linuxInfo = await linux.GetInfoAsync();
        var linuxSample = await linux.ReadAsync();
        Assert(linuxInfo.Cpu.LogicalProcessorCount > 0, "Linux performance source did not report logical processors.");
        Assert(linuxSample.Memory.TotalBytes > 0, "Linux performance source did not read MemTotal.");
        Assert(linuxSample.Cpu.LogicalProcessors.Count > 0, "Linux performance source did not read per-logical-CPU counters.");
        Assert(linuxInfo.Filesystems.All(filesystem => filesystem.MountPoint == "/"), "Linux performance source reported a non-root filesystem.");
        Assert(linuxSample.Filesystems.Count <= 1, "Linux performance source reported more than the root filesystem.");
    }

    var source = new FakePerformanceSource();
    var history = new PerformanceHistory();
    var sampler = new PerformanceSampler(source, history);
    await sampler.StartAsync(CancellationToken.None);
    try
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var latest = sampler.GetLatest() ?? throw new InvalidOperationException("Performance sampler did not publish its second sample.");
        Assert(latest.Sequence == 1, "Performance sampler did not begin its sequence at the first valid sample.");
        Assert(latest.Cpu.TotalPercent == 30, "CPU utilization did not use adjacent raw counters.");
        Assert(latest.Disks.Single().ReadBytesPerSecond == 5120, "Disk byte rate did not use sector deltas.");
        Assert(latest.Networks.Single().ReceiveBytesPerSecond == 500, "Network rate did not use adjacent counters.");
        Assert(sampler.GetHistory(60).Count == 1, "Performance history did not retain the valid sample.");
    }
    finally
    {
        await sampler.StopAsync(CancellationToken.None);
        sampler.Dispose();
    }
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

    var resolveManagedExecutable = typeof(NginxWebServerManager).GetMethod("ResolveManagedExecutablePath", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Managed Nginx executable resolver was not found.");
    var managedExecutable = (string)resolveManagedExecutable.Invoke(null, [managedRoot, true])!;
    if (OperatingSystem.IsLinux())
        Assert(managedExecutable == "/usr/sbin/nginx", "Built-in Linux installation must use the package executable instead of creating a second copy.");

    var resolveManagedConfiguration = typeof(NginxWebServerManager).GetMethod("ResolveManagedConfigurationPath", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Managed Nginx configuration resolver was not found.");
    var managedConfigurationPath = (string)resolveManagedConfiguration.Invoke(null, [managedRoot, true])!;
    if (OperatingSystem.IsLinux())
        Assert(managedConfigurationPath == "/etc/nginx/nginx.conf", "Built-in Linux installation must use the package configuration managed by nginx.service.");

    if (OperatingSystem.IsLinux())
    {
        var isPosixProcessAlive = typeof(NginxWebServerManager).GetMethod("IsPosixProcessAlive", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Linux process liveness checker was not found.");
        Assert((bool)isPosixProcessAlive.Invoke(null, [Environment.ProcessId])!, "The current Linux process was not recognized as alive.");
        Assert(!(bool)isPosixProcessAlive.Invoke(null, [int.MaxValue])!, "A non-existent Linux process was reported as alive.");
    }

    var validServerName = typeof(NginxWebServerManager).GetMethod("IsValidServerName", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx server-name validator was not found.");
    Assert((bool)validServerName.Invoke(null, ["192.0.2.10"])!, "IPv4 addresses were rejected as Nginx server names.");
    Assert((bool)validServerName.Invoke(null, ["2001:db8::10"])!, "IPv6 addresses were rejected as Nginx server names.");
    Assert(!(bool)validServerName.Invoke(null, ["example.com; return 200"])!, "Unsafe Nginx server name was accepted.");

    var isNginxProcessName = typeof(NginxWebServerManager).GetMethod("IsNginxProcessName", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx process-name matcher was not found.");
    Assert((bool)isNginxProcessName.Invoke(null, ["nginx"])!, "The normal Nginx process name was rejected.");
    Assert((bool)isNginxProcessName.Invoke(null, ["nginx: master process /usr/sbin/nginx"])!, "The Linux Nginx master-process name was rejected.");
    Assert((bool)isNginxProcessName.Invoke(null, ["nginx: worker process"])!, "The Linux Nginx worker-process name was rejected.");
    Assert(!(bool)isNginxProcessName.Invoke(null, ["nginx-helper"])!, "An unrelated process name was accepted as Nginx.");

    var multiPortSite = new WebServerSiteDto("multi-port", "nginx-test", "multi-port", WebServerSiteKind.Static,
        ["app.example.test", "admin.example.test"], 5000, null, "/srv/remoteos-sites/multi-port", null, false, DateTimeOffset.UtcNow,
        [new WebServerSiteBindingDto("app.example.test", 5000), new WebServerSiteBindingDto("admin.example.test", 6000)]);
    Assert(multiPortSite.DomainsDisplay == "app.example.test:5000, admin.example.test:6000", "Multi-port bindings were not formatted for the site table.");
    var renderSite = typeof(NginxWebServerManager).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == "RenderSiteConfiguration" && method.GetParameters().Length == 2);
    var rendered = (string)renderSite.Invoke(null, [multiPortSite, null])!;
    Assert(rendered.Split("server {", StringSplitOptions.None).Length == 2 && rendered.Contains("listen 5000;") && rendered.Contains("listen 6000;")
        && rendered.Contains("server_name app.example.test admin.example.test;"), "A multi-port site was not rendered as one Nginx server with all listeners and names.");
    var proxySite = multiPortSite with { Id = "proxy-site", Kind = WebServerSiteKind.ReverseProxy, RootPath = null, Upstream = "http://127.0.0.1:5090" };
    var renderedProxy = (string)renderSite.Invoke(null, [proxySite, null])!;
    Assert(renderedProxy.Contains("proxy_http_version 1.1;")
        && renderedProxy.Contains("proxy_set_header Upgrade $http_upgrade;")
        && renderedProxy.Contains("proxy_set_header Connection \"upgrade\";"), "A reverse-proxy site did not preserve WebSocket upgrades for SignalR.");
    var configurationTestProblem = typeof(NginxWebServerManager).GetMethod("ConfigurationTestProblem", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx configuration-test problem classifier was not found.");
    Assert((string)configurationTestProblem.Invoke(null, ["[emerg] host not found in upstream \"locahost\""])! == "webserver.site_upstream_unresolvable",
        "An unresolvable reverse-proxy upstream was not given a specific error.");
    Assert((string)configurationTestProblem.Invoke(null, ["[emerg] unexpected \"}\""])! == "webserver.site_config_test_failed",
        "An unrelated Nginx configuration error was misclassified as an upstream-resolution error.");
    var renderWithAcme = typeof(NginxWebServerManager).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == "RenderSiteConfiguration" && method.GetParameters().Length == 3);
    var renderedWithAcme = (string)renderWithAcme.Invoke(null, [proxySite, null, "/var/lib/remoteos/acme-challenge"])!;
    Assert(renderedWithAcme.Contains("location ^~ /.well-known/acme-challenge/")
        && renderedWithAcme.Contains("alias /var/lib/remoteos/acme-challenge/;")
        && renderedWithAcme.Contains("location / {"), "ACME HTTP-01 routing was not rendered ahead of the site location.");

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

static async Task VerifyTrackedWorkspaceWallpaperUpdateAsync(string root)
{
    var databasePath = Path.Combine(root, "workspace-preferences.db");
    var options = new DbContextOptionsBuilder<RemoteOsDbContext>()
        .UseSqlite($"Data Source={databasePath}")
        .Options;
    var workspaceId = Guid.NewGuid();
    var originalMapping = new DefaultAppMappingDto("https", "remoteos.browser");
    var themePreferences = new ThemePreferencesDto
    {
        PaletteId = "custom:test-palette",
        CustomPalettes =
        [
            new ThemePaletteDto
            {
                Id = "test-palette",
                Name = "Persistence test",
                LightColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accent"] = "#0078D4",
                    ["Shadow"] = "#22000000",
                },
                DarkColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accent"] = "#89B4FA",
                    ["Shadow"] = "#66000000",
                },
            },
        ],
    };

    await using (var db = new RemoteOsDbContext(options))
    {
        await db.Database.EnsureCreatedAsync();
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            UserId = Guid.NewGuid(),
            Name = "Preference update regression test",
            CreatedAt = DateTimeOffset.UtcNow,
            Preferences = new WorkspacePreferencesDto(
                WorkspacePreferencesDto.BuiltInWallpaperPrefix + "bloom", ThemeKind.Light,
                WorkspacePreferencesDto.TimeFormat24H, "yyyy/M/d", "en-US", "en-US", [originalMapping],
                ThemePreferences: themePreferences)
        });
        await db.SaveChangesAsync();
    }

    var customWallpaperKey = WorkspacePreferencesDto.CustomWallpaperPrefix + Guid.NewGuid().ToString("N");
    await using (var db = new RemoteOsDbContext(options))
    {
        var repository = new SqliteWorkspaceRepository(db);
        var workspace = repository.FindById(workspaceId) ?? throw new InvalidOperationException("Workspace was not loaded.");
        workspace.Preferences.WallpaperKey = customWallpaperKey;
        repository.Update(workspace);
    }

    await using (var db = new RemoteOsDbContext(options))
    {
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId);
        Assert(workspace.Preferences.WallpaperKey == customWallpaperKey, "Wallpaper key was not persisted.");
        Assert(workspace.Preferences.DefaultApps.SequenceEqual([originalMapping]), "Changing the wallpaper modified default-app mappings.");
        Assert(workspace.Preferences.ThemePreferences?.CustomPalettes.Single().LightColors?["Accent"] == "#0078D4",
            "Custom theme palette colors were not persisted.");
    }
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

sealed class FixtureHttpClientFactory(byte[] payload) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new FixtureHandler(payload));
    private sealed class FixtureHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }
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

sealed class FakePerformanceSource : ISystemPerformanceSource
{
    private int _sample;

    public ValueTask<RemoteOS.Protocol.SystemMonitor.PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new RemoteOS.Protocol.SystemMonitor.PerformanceInfoDto(
            new("test", 1, 1, null, null), new(1000, 0), [new("fs:test", "test", "/test")],
            [new("disk:test", "test", null, [])], [new("net:test", "test", null, [])], new(true, false, false, true, false, true, true, false)));

    public ValueTask<RawPerformanceSample> ReadAsync(CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _sample);
        return ValueTask.FromResult(new RawPerformanceSample(DateTimeOffset.UtcNow, index * System.Diagnostics.Stopwatch.Frequency,
            new RawCpuTimes(index * 100, index * 70, index * 20, index * 10, null, [new(index * 100, index * 70, index * 20, index * 10, null, [], null)], null),
            new RawMemory(1000, 600, null, null, 0, 0), [new("fs:test", 1000, 600)],
            [new("disk:test", index * 10, index * 5, index * 10, index * 5, index * 100, index * 100, index * 10, index * 5, 512)],
            [new("net:test", index * 500, index * 100, index * 5, index, 0, 0, 0, 0)], index));
    }
}
