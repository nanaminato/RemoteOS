using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RemoteOS.Protocol.WebServers;

namespace Server.WebServer;

/// <summary>
/// Nginx V1 provider. It uses fixed executable arguments and only creates one owned,
/// marker-bearing file in an already-included conf.d directory. It is deliberately below
/// <see cref="IWebServerManager"/> so callers use the product-neutral facade and Nginx remains
/// an optional host integration.
/// </summary>
internal sealed partial class NginxWebServerManager(
    IHostPrivilegeService privileges,
    WebServerOperationStore operations,
    WebServerMetadataRepository metadata,
    IHostApplicationLifetime lifetime,
    NginxManagedOptions managedOptions,
    NginxInstallPackageStore packages,
    ILogger<NginxWebServerManager> logger) : IWebServerProvider
{
    private const string ProviderKey = "nginx";
    private const string OwnedFileName = "remoteos.conf";
    private const string OwnershipMarker = "# Managed by RemoteOS. Do not edit.";
    private const string ManagedMarkerName = ".remoteos-managed";
    private const string ManagedMarkerContent = "RemoteOS owns this Nginx installation. Do not move this marker.\n";
    private static readonly string OwnedContent = $"{OwnershipMarker}\n# RemoteOS-owned Nginx integration anchor.\n";
    private static readonly string ManagedConfiguration = """
        worker_processes auto;
        error_log logs/error.log notice;
        pid logs/nginx.pid;

        events { worker_connections 1024; }

        http {
            default_type application/octet-stream;
            access_log logs/access.log;
            sendfile on;
            include conf.d/*.conf;
        }
        """;
    private static readonly SemaphoreSlim IntegrationGate = new(1, 1);
    // A managed instance has one fixed root, so concurrent installs must serialize their
    // directory checks, replacement, and extraction.
    private static readonly SemaphoreSlim ManagedInstallGate = new(1, 1);

    public string ProviderId => ProviderKey;

    public async Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discovered = new List<WebServerDto>();
        var managed = GetManagedLayout();
        if (IsManagedInstallation(managed))
        {
            var instance = await DetectAsync(managed.ExecutablePath, WebServerManagementMode.Managed, cancellationToken);
            if (instance is not null) discovered.Add(instance);
        }

        foreach (var executable in FindExternalExecutables())
        {
            if (string.Equals(executable, managed.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
            var instance = await DetectAsync(executable, null, cancellationToken);
            if (instance is not null) discovered.Add(instance);
        }
        return discovered;
    }

    public async Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null || instance.Id != instanceId) return null;
        var running = IsNginxRunning();
        return new WebServerStatusDto(instanceId, running ? WebServerRuntimeState.Running : WebServerRuntimeState.Stopped);
    }

    public async Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken)
    {
        var detected = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (detected is null || detected.Id != instanceId) return null;
        var arguments = detected.ManagementMode == WebServerManagementMode.Managed
            ? ManagedArguments(GetManagedLayout(), ["-t"])
            : new[] { "-t" };
        var result = await RunNginxAsync(detected.ExecutablePath, arguments, cancellationToken);
        return new WebServerConfigTestResultDto(result.Success, result.Success ? "" : "webserver.config_test_failed");
    }

    public async Task<WebServerOperationDto?> IntegrateAsync(string instanceId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        var detected = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (detected is null || detected.Id != instanceId) return null;
        if (!request.Confirmed)
            return new WebServerOperationDto(Guid.Empty, instanceId, "integrate", WebServerOperationState.Failed, "validation", "webserver.confirmation_required", null, null, DateTimeOffset.UtcNow);
        if (!privileges.IsAdministrator)
            return new WebServerOperationDto(Guid.Empty, instanceId, "integrate", WebServerOperationState.Failed, "authorization", "webserver.config_elevation_required", null, null, DateTimeOffset.UtcNow);
        return await operations.StartAsync(idempotencyKey, instanceId, "integrate", actor, ct => IntegrateCoreAsync(detected, ct), lifetime.ApplicationStopping);
    }

    public async Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        var detected = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (detected is null || detected.Id != instanceId) return null;
        if (detected.ManagementMode != WebServerManagementMode.Integrated)
            return new WebServerOperationDto(Guid.Empty, instanceId, "reload", WebServerOperationState.Failed, "authorization", "webserver.reload_not_permitted", null, null, DateTimeOffset.UtcNow);
        if (!privileges.IsAdministrator)
            return new WebServerOperationDto(Guid.Empty, instanceId, "reload", WebServerOperationState.Failed, "authorization", "webserver.lifecycle_elevation_required", null, null, DateTimeOffset.UtcNow);
        return await operations.StartAsync(idempotencyKey, instanceId, "reload", actor, async ct =>
            new WebServerOperationResult((await RunNginxAsync(detected.ExecutablePath, ["-s", "reload"], ct)).Success ? "" : "webserver.reload_failed"), lifetime.ApplicationStopping);
    }

    public async Task<WebServerOperationDto?> InstallManagedAsync(string idempotencyKey, InstallManagedWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        var layout = GetManagedLayout();
        if (!request.Confirmed)
            return Rejected(layout.InstanceId, "install", "webserver.confirmation_required");
        if (!privileges.IsAdministrator)
            return Rejected(layout.InstanceId, "install", "webserver.install_elevation_required");
        if (IsManagedInstallation(layout))
            return Rejected(layout.InstanceId, "install", "webserver.managed_already_installed");
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(request.PackageId) && string.IsNullOrWhiteSpace(request.Version))
            return Rejected(layout.InstanceId, "install", "webserver.version_required");
        if (OperatingSystem.IsWindows() && ManagedRootExists(layout)
            && request.ExistingDirectoryAction == ManagedInstallExistingDirectoryAction.Reject)
            return Rejected(layout.InstanceId, "install", "webserver.managed_installation_exists");
        if (!OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(request.Version) && !LinuxPackageVersionPattern().IsMatch(request.Version.Trim()))
            return Rejected(layout.InstanceId, "install", "webserver.version_invalid");
        if (!OperatingSystem.IsWindows() && !IsConfiguredInstaller() && !CanUseBuiltInInstaller())
            return Rejected(layout.InstanceId, "install", "webserver.install_unsupported_platform");
        return await operations.StartAsync(idempotencyKey, layout.InstanceId, "install", actor,
            (progress, ct) => InstallManagedCoreAsync(layout, request, progress, ct), lifetime.ApplicationStopping);
    }

    public async Task<WebServerInstallPackageDto?> UploadManagedPackageAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        logger.LogInformation("Received request to upload a Windows Nginx installation package. FileName={FileName}", Path.GetFileName(fileName));
        var packageId = await packages.SaveAsync(fileName, content, cancellationToken);
        if (packageId is null)
            logger.LogWarning("Windows Nginx package upload was rejected. FileName={FileName}", Path.GetFileName(fileName));
        return packageId is null ? null : new WebServerInstallPackageDto(packageId, Path.GetFileName(fileName));
    }

    public async Task<WebServerInstallCatalogDto?> GetManagedInstallCatalogAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new WebServerInstallCatalogDto(null, null, []);
        try
        {
            logger.LogInformation("Retrieving Windows Nginx version catalog from the official download page.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var page = await client.GetStringAsync("https://nginx.org/en/download.html", cancellationToken);
            var versions = WindowsDownloadVersionPattern().Matches(page).Select(match => match.Groups["version"].Value)
                .Distinct(StringComparer.Ordinal).OrderByDescending(version => Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0)).ToArray();
            logger.LogInformation("Retrieved Windows Nginx version catalog. Versions={VersionCount}", versions.Length);
            if (versions.Length == 0)
            {
                logger.LogWarning("The official Nginx download page did not contain any Windows versions.");
                return new WebServerInstallCatalogDto(null, null, [], "webserver.version_catalog_unavailable");
            }
            return new WebServerInstallCatalogDto(
                FirstWindowsVersionInSection(page, "Mainline version", "Stable version"),
                FirstWindowsVersionInSection(page, "Stable version", "Legacy versions"), versions);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Failed to retrieve the Windows Nginx version catalog.");
            return new WebServerInstallCatalogDto(null, null, [], "webserver.version_catalog_unavailable");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Timed out while retrieving the Windows Nginx version catalog.");
            return new WebServerInstallCatalogDto(null, null, [], "webserver.version_catalog_unavailable");
        }
    }

    public async Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null) return null;
        if (!privileges.IsAdministrator)
            return Rejected(instanceId, action.ToString().ToLowerInvariant(), "webserver.lifecycle_elevation_required");
        if (action == WebServerLifecycleAction.Reload)
        {
            if (instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed))
                return Rejected(instanceId, "reload", "webserver.reload_not_permitted");
            var arguments = instance.ManagementMode == WebServerManagementMode.Managed
                ? ManagedArguments(GetManagedLayout(), ["-s", "reload"])
                : new[] { "-s", "reload" };
            return await operations.StartAsync(idempotencyKey, instanceId, "reload", actor, async ct =>
                new WebServerOperationResult((await RunNginxAsync(instance.ExecutablePath, arguments, ct)).Success ? "" : "webserver.reload_failed"), lifetime.ApplicationStopping);
        }
        if (instance.ManagementMode != WebServerManagementMode.Managed)
            return Rejected(instanceId, action.ToString().ToLowerInvariant(), "webserver.managed_required");

        var layout = GetManagedLayout();
        return await operations.StartAsync(idempotencyKey, instanceId, action.ToString().ToLowerInvariant(), actor,
            ct => ApplyManagedLifecycleCoreAsync(layout, action, ct), lifetime.ApplicationStopping);
    }

    public async Task<WebServerOperationDto?> UninstallManagedAsync(string instanceId, string idempotencyKey, UninstallManagedWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null) return null;
        if (!request.Confirmed) return Rejected(instanceId, "uninstall", "webserver.confirmation_required");
        if (!privileges.IsAdministrator) return Rejected(instanceId, "uninstall", "webserver.install_elevation_required");
        if (instance.ManagementMode != WebServerManagementMode.Managed) return Rejected(instanceId, "uninstall", "webserver.managed_required");
        return await operations.StartAsync(idempotencyKey, instanceId, "uninstall", actor,
            ct => UninstallManagedCoreAsync(GetManagedLayout(), ct), lifetime.ApplicationStopping);
    }

    private async Task<WebServerDto?> DetectAsync(string executable, WebServerManagementMode? forcedMode, CancellationToken cancellationToken)
    {
        var details = await RunNginxAsync(executable, ["-V"], cancellationToken);
        if (!details.Success && string.IsNullOrWhiteSpace(details.Output)) return null;
        var configPath = forcedMode == WebServerManagementMode.Managed
            ? GetManagedLayout().ConfigurationPath
            : ParseConfigPath(details.Output, executable);
        var version = VersionPattern().Match(details.Output) is { Success: true } match ? match.Groups["version"].Value : null;
        var includeDirectory = configPath is null ? null : FindOwnedIncludeDirectory(configPath);
        var ownedPath = includeDirectory is null ? null : Path.Combine(includeDirectory, OwnedFileName);
        var integrated = ownedPath is not null && IsOwnedFile(ownedPath);
        var mode = forcedMode ?? (integrated ? WebServerManagementMode.Integrated : WebServerManagementMode.External);
        var isManaged = mode == WebServerManagementMode.Managed;
        var capabilities = new WebServerCapabilities(
            CanRead: true,
            CanTestConfiguration: true,
            CanIntegrate: !isManaged && !integrated && privileges.IsAdministrator && includeDirectory is not null,
            CanReload: (integrated || isManaged) && privileges.IsAdministrator,
            CanStart: isManaged && privileges.IsAdministrator,
            CanStop: isManaged && privileges.IsAdministrator,
            CanRestart: isManaged && privileges.IsAdministrator,
            CanUninstall: isManaged && privileges.IsAdministrator);
        var instance = new WebServerDto(InstanceId(executable), ProviderKey, WebServerType.Nginx, mode, executable, configPath, version, DateTimeOffset.UtcNow, capabilities);
        await metadata.UpsertInstanceAsync(instance, cancellationToken);
        return instance;
    }

    private async Task<WebServerOperationResult> IntegrateCoreAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        if (instance.ConfigurationPath is null) return new WebServerOperationResult("webserver.configuration_not_found");
        var snapshot = await metadata.CreateSnapshotAsync(instance, cancellationToken);
        if (snapshot is null) return new WebServerOperationResult("webserver.configuration_not_found");
        var includeDirectory = FindOwnedIncludeDirectory(instance.ConfigurationPath);
        if (includeDirectory is null) return new WebServerOperationResult("webserver.include_context_not_supported", snapshot.Id);
        if (Path.GetFileName(includeDirectory) is not "conf.d") return new WebServerOperationResult("webserver.include_context_not_supported", snapshot.Id);
        if (IsSymbolicLink(includeDirectory)) return new WebServerOperationResult("webserver.unsafe_path", snapshot.Id);

        var destination = Path.Combine(includeDirectory, OwnedFileName);
        if (Path.GetFullPath(destination) != destination || IsSymbolicLink(destination)) return new WebServerOperationResult("webserver.unsafe_path", snapshot.Id);
        if (File.Exists(destination)) return new WebServerOperationResult(IsOwnedFile(destination) ? "" : "webserver.ownership_conflict", snapshot.Id);

        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the per-provider transaction lock so an external change cannot be overwritten.
            if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
                return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
            if (File.Exists(destination)) return new WebServerOperationResult(IsOwnedFile(destination) ? "" : "webserver.ownership_conflict", snapshot.Id);
            // Keep the staged file in the include graph (and on the same filesystem), so
            // nginx -t validates the exact file that will be atomically renamed into place.
            var stage = Path.Combine(includeDirectory, $"remoteos.{Guid.NewGuid():N}.conf");
            var committed = false;
            try
            {
                await File.WriteAllTextAsync(stage, OwnedContent, new UTF8Encoding(false), cancellationToken);
                if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
                    return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
                var test = await RunNginxAsync(instance.ExecutablePath, ["-t"], cancellationToken);
                if (!test.Success)
                    return new WebServerOperationResult("webserver.config_test_failed", snapshot.Id);
                if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
                    return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
                File.Move(stage, destination, false);
                committed = true;
                var reload = await RunNginxAsync(instance.ExecutablePath, ["-s", "reload"], cancellationToken);
                if (reload.Success) return new WebServerOperationResult("", snapshot.Id);

                // The old workers normally keep the prior configuration. Restore disk state and attempt a rollback reload.
                if (!DeleteOwnedFile(destination)) return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
                _ = await RunNginxAsync(instance.ExecutablePath, ["-t"], cancellationToken);
                _ = await RunNginxAsync(instance.ExecutablePath, ["-s", "reload"], cancellationToken);
                return new WebServerOperationResult("webserver.reload_failed", snapshot.Id);
            }
            catch
            {
                // Cancellation and unexpected process errors must not leave an unverified disk config behind.
                if (committed) _ = DeleteOwnedFile(destination);
                throw;
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.config_elevation_required", snapshot.Id); }
        catch (IOException) { return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id); }
        finally { IntegrationGate.Release(); }
    }

    private async Task<WebServerOperationResult> InstallManagedCoreAsync(ManagedLayout layout, InstallManagedWebServerRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return await InstallWindowsManagedAsync(layout, request, progress, cancellationToken);
        await progress.ReportAsync(IsConfiguredInstaller() ? "installer_running" : "installing_package", cancellationToken);
        var started = await RunInstallerAsync(layout, request.Version, cancellationToken);
        if (!started) return new WebServerOperationResult("webserver.install_failed");
        await progress.ReportAsync("verifying_layout", cancellationToken);
        if (!File.Exists(layout.ExecutablePath) || IsSymbolicLink(layout.ExecutablePath) || !Directory.Exists(layout.Root) || IsSymbolicLink(layout.Root))
            return new WebServerOperationResult("webserver.install_layout_invalid");
        try
        {
            await progress.ReportAsync("validating_configuration", cancellationToken);
            await File.WriteAllTextAsync(layout.MarkerPath, ManagedMarkerContent, new UTF8Encoding(false), cancellationToken);
            var test = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);
            if (test.Success)
            {
                await progress.ReportAsync("finalizing", cancellationToken);
                return new WebServerOperationResult("");
            }
            File.Delete(layout.MarkerPath);
            return new WebServerOperationResult("webserver.config_test_failed");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.install_layout_invalid"); }
    }

    private async Task<WebServerOperationResult> InstallWindowsManagedAsync(ManagedLayout layout, InstallManagedWebServerRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        await ManagedInstallGate.WaitAsync(cancellationToken);
        try { return await InstallWindowsManagedCoreAsync(layout, request, progress, cancellationToken); }
        finally { ManagedInstallGate.Release(); }
    }

    private async Task<WebServerOperationResult> InstallWindowsManagedCoreAsync(ManagedLayout layout, InstallManagedWebServerRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        string? packageId = request.PackageId;
        var replaceExisting = false;
        logger.LogInformation("Starting managed Windows Nginx installation. Version={Version}, UsesUploadedPackage={UsesUploadedPackage}", request.Version, !string.IsNullOrWhiteSpace(packageId));
        try
        {
            if (ManagedRootExists(layout))
            {
                if (request.ExistingDirectoryAction == ManagedInstallExistingDirectoryAction.Reuse)
                    return await ValidateAndMarkWindowsManagedInstallationAsync(layout, progress, cancellationToken);
                if (request.ExistingDirectoryAction != ManagedInstallExistingDirectoryAction.Replace)
                    return new WebServerOperationResult("webserver.managed_installation_exists");
                replaceExisting = true;
            }
            if (string.IsNullOrWhiteSpace(packageId))
            {
                var version = string.IsNullOrWhiteSpace(request.Version) ? "1.31.3" : request.Version.Trim();
                if (!WindowsVersionPattern().IsMatch(version))
                {
                    logger.LogWarning("Rejected Windows Nginx installation due to an invalid version. Version={Version}", version);
                    return new WebServerOperationResult("webserver.version_invalid");
                }
                await progress.ReportAsync("downloading", cancellationToken);
                logger.LogInformation("Downloading Windows Nginx ZIP from the official source. Version={Version}", version);
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var response = await client.GetAsync($"https://nginx.org/download/nginx-{version}.zip", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Official Windows Nginx download failed. Version={Version}, StatusCode={StatusCode}", version, (int)response.StatusCode);
                    return new WebServerOperationResult("webserver.download_failed");
                }
                await using var download = await response.Content.ReadAsStreamAsync(cancellationToken);
                packageId = await packages.SaveAsync($"nginx-{version}.zip", download, cancellationToken);
                if (packageId is null)
                {
                    logger.LogWarning("Downloaded Windows Nginx ZIP failed validation. Version={Version}", version);
                    return new WebServerOperationResult("webserver.package_invalid");
                }
            }

            var archivePath = packages.GetPath(packageId);
            if (archivePath is null)
            {
                logger.LogWarning("Windows Nginx installation package is unavailable. PackageId={PackageId}", packageId);
                return new WebServerOperationResult("webserver.package_not_found");
            }
            if (replaceExisting)
            {
                // Download and validate the replacement before deleting a working installation.
                await progress.ReportAsync("removing_existing_installation", cancellationToken);
                if (!DeleteReplaceableWindowsInstallation(layout))
                    return new WebServerOperationResult("webserver.existing_installation_unsafe");
            }
            await progress.ReportAsync("extracting", cancellationToken);
            var extracted = ExtractWindowsPackage(layout, archivePath);
            if (!extracted)
            {
                logger.LogWarning("Windows Nginx package extraction or layout validation failed. PackageId={PackageId}", packageId);
                return new WebServerOperationResult("webserver.package_invalid");
            }
            var finalized = await ValidateAndMarkWindowsManagedInstallationAsync(layout, progress, cancellationToken);
            if (finalized.ProblemCode.Length == 0)
                logger.LogInformation("Managed Windows Nginx installation completed. PackageId={PackageId}", packageId);
            return finalized;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Windows Nginx download request failed.");
            return new WebServerOperationResult("webserver.download_failed");
        }
        finally { packages.Delete(packageId); }
    }

    private async Task<WebServerOperationResult> ValidateAndMarkWindowsManagedInstallationAsync(ManagedLayout layout, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            await progress.ReportAsync("verifying_layout", cancellationToken);
            if (!IsReusableWindowsInstallation(layout))
                return new WebServerOperationResult("webserver.existing_installation_unsafe");
            await progress.ReportAsync("validating_configuration", cancellationToken);
            var test = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);
            if (!test.Success) return new WebServerOperationResult("webserver.config_test_failed");
            await progress.ReportAsync("finalizing", cancellationToken);
            await WriteManagedMarkerAsync(layout.MarkerPath, cancellationToken);
            logger.LogInformation("Validated and marked Windows Nginx installation as RemoteOS-managed. Destination={Destination}", layout.Root);
            return new WebServerOperationResult("");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to validate and mark a Windows Nginx installation. Destination={Destination}", layout.Root);
            return new WebServerOperationResult("webserver.existing_installation_unsafe");
        }
    }

    private bool ExtractWindowsPackage(ManagedLayout layout, string archivePath)
    {
        var staging = $"{layout.Root}.staging-{Guid.NewGuid():N}";
        try
        {
            if (Directory.Exists(layout.Root) || File.Exists(layout.Root) || IsSymbolicLink(layout.Root))
            {
                logger.LogWarning("Cannot extract Windows Nginx package because the managed destination already exists or is unsafe. Destination={Destination}", layout.Root);
                return false;
            }
            logger.LogInformation("Extracting Windows Nginx ZIP into a staging directory. Destination={Destination}", layout.Root);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (!archive.Entries.All(entry => NginxInstallPackageStore.IsSafeEntry(entry.FullName)))
                {
                    logger.LogWarning("Rejected Windows Nginx ZIP because it contains an unsafe archive entry.");
                    return false;
                }
                archive.ExtractToDirectory(staging);
            }
            var executable = Directory.GetFiles(staging, "nginx.exe", SearchOption.AllDirectories).SingleOrDefault();
            if (executable is null)
            {
                logger.LogWarning("Rejected Windows Nginx ZIP because nginx.exe was not found after extraction.");
                return false;
            }
            var extractedRoot = Path.GetDirectoryName(executable)!;
            if (!File.Exists(Path.Combine(extractedRoot, "conf", "nginx.conf")))
            {
                logger.LogWarning("Rejected Windows Nginx ZIP because conf/nginx.conf was not found next to nginx.exe.");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(layout.Root)!);
            Directory.Move(extractedRoot, layout.Root);
            var validLayout = File.Exists(layout.ExecutablePath) && File.Exists(layout.ConfigurationPath);
            if (!validLayout)
                logger.LogWarning("Windows Nginx extraction completed but the managed layout is incomplete. Destination={Destination}", layout.Root);
            return validLayout;
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "Windows Nginx ZIP could not be read during extraction.");
            return false;
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "I/O failure while extracting Windows Nginx ZIP. Destination={Destination}", layout.Root);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Access denied while extracting Windows Nginx ZIP. Destination={Destination}", layout.Root);
            return false;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static bool ManagedRootExists(ManagedLayout layout)
        => Directory.Exists(layout.Root) || File.Exists(layout.Root) || IsSymbolicLink(layout.Root);

    private static async Task WriteManagedMarkerAsync(string markerPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(ManagedMarkerContent), cancellationToken);
    }

    private static bool IsReusableWindowsInstallation(ManagedLayout layout)
    {
        if (!Directory.Exists(layout.Root) || IsSymbolicLink(layout.Root)
            || !File.Exists(layout.ExecutablePath) || !File.Exists(layout.ConfigurationPath)
            || IsSymbolicLink(layout.ExecutablePath) || IsSymbolicLink(layout.ConfigurationPath)
            || File.Exists(layout.MarkerPath) || IsSymbolicLink(layout.MarkerPath)) return false;
        try
        {
            return Directory.EnumerateFileSystemEntries(layout.Root, "*", SearchOption.AllDirectories)
                .All(path => !IsSymbolicLink(path));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool DeleteReplaceableWindowsInstallation(ManagedLayout layout)
    {
        if (!IsReusableWindowsInstallation(layout))
        {
            logger.LogWarning("Refused to replace an unsafe or incomplete existing Windows Nginx installation. Destination={Destination}", layout.Root);
            return false;
        }
        try
        {
            Directory.Delete(layout.Root, recursive: true);
            logger.LogInformation("Removed existing Windows Nginx installation before replacement. Destination={Destination}", layout.Root);
            return true;
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to remove existing Windows Nginx installation. Destination={Destination}", layout.Root);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Access denied while removing existing Windows Nginx installation. Destination={Destination}", layout.Root);
            return false;
        }
    }

    private static string? FirstWindowsVersionInSection(string page, string startHeading, string endHeading)
    {
        var start = page.IndexOf(startHeading, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var end = page.IndexOf(endHeading, start + startHeading.Length, StringComparison.OrdinalIgnoreCase);
        var section = page[start..(end < 0 ? page.Length : end)];
        return WindowsDownloadVersionPattern().Match(section) is { Success: true } match ? match.Groups["version"].Value : null;
    }

    private async Task<WebServerOperationResult> ApplyManagedLifecycleCoreAsync(ManagedLayout layout, WebServerLifecycleAction action, CancellationToken cancellationToken)
    {
        var result = action switch
        {
            WebServerLifecycleAction.Start => await StartManagedAsync(layout, cancellationToken),
            WebServerLifecycleAction.Stop => await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken),
            WebServerLifecycleAction.Restart => await RestartManagedAsync(layout, cancellationToken),
            _ => new CommandResult(false, "")
        };
        return new WebServerOperationResult(result.Success ? "" : $"webserver.{action.ToString().ToLowerInvariant()}_failed");
    }

    private async Task<WebServerOperationResult> UninstallManagedCoreAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        if (!IsManagedInstallation(layout)) return new WebServerOperationResult("webserver.managed_required");
        _ = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken);
        try
        {
            Directory.Delete(layout.Root, recursive: true);
            return new WebServerOperationResult("");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.uninstall_failed"); }
    }

    private async Task<CommandResult> StartManagedAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        var test = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);
        return !test.Success ? test : await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, []), cancellationToken);
    }

    private async Task<CommandResult> RestartManagedAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        _ = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken);
        return await StartManagedAsync(layout, cancellationToken);
    }

    private bool IsConfiguredInstaller() => Path.IsPathFullyQualified(managedOptions.InstallerCommand) && File.Exists(managedOptions.InstallerCommand);

    private static bool CanUseBuiltInInstaller() => OperatingSystem.IsLinux() && File.Exists("/usr/bin/apt-get");

    private Task<bool> RunInstallerAsync(ManagedLayout layout, string? version, CancellationToken cancellationToken) =>
        IsConfiguredInstaller()
            ? RunConfiguredInstallerAsync(cancellationToken)
            : RunBuiltInLinuxInstallerAsync(layout, version, cancellationToken);

    private async Task<bool> RunConfiguredInstallerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(managedOptions.InstallerCommand) { UseShellExecute = false, CreateNoWindow = true } };
            foreach (var argument in managedOptions.InstallerArguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch { return false; }
    }

    private async Task<bool> RunBuiltInLinuxInstallerAsync(ManagedLayout layout, string? version, CancellationToken cancellationToken)
    {
        if (!CanUseBuiltInInstaller()) return false;
        if (!await RunProcessAsync("/usr/bin/apt-get", ["update"], cancellationToken)) return false;
        var package = string.IsNullOrWhiteSpace(version) ? "nginx" : $"nginx={version.Trim()}";
        if (!await RunProcessAsync("/usr/bin/apt-get", ["install", "--yes", "--no-install-recommends", package], cancellationToken)) return false;
        const string systemExecutable = "/usr/sbin/nginx";
        if (!File.Exists(systemExecutable) || IsSymbolicLink(systemExecutable)) return false;
        try
        {
            if (IsSymbolicLink(layout.Root)) return false;
            Directory.CreateDirectory(Path.Combine(layout.Root, "sbin"));
            Directory.CreateDirectory(Path.Combine(layout.Root, "conf", "conf.d"));
            Directory.CreateDirectory(Path.Combine(layout.Root, "logs"));
            File.Copy(systemExecutable, layout.ExecutablePath, overwrite: true);
            await File.WriteAllTextAsync(layout.ConfigurationPath, ManagedConfiguration, new UTF8Encoding(false), cancellationToken);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<bool> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true } };
            process.StartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch { return false; }
    }

    private ManagedLayout GetManagedLayout()
    {
        var root = string.IsNullOrWhiteSpace(managedOptions.InstallationRoot)
            ? OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteOS", "webserver", "nginx")
                : "/var/lib/remoteos/webserver/nginx"
            : managedOptions.InstallationRoot;
        root = Path.GetFullPath(root);
        var executable = OperatingSystem.IsWindows() ? Path.Combine(root, "nginx.exe") : Path.Combine(root, "sbin", "nginx");
        return new ManagedLayout(root, executable, Path.Combine(root, "conf", "nginx.conf"), Path.Combine(root, ManagedMarkerName), InstanceId(executable));
    }

    private static bool IsManagedInstallation(ManagedLayout layout)
    {
        try
        {
            return File.Exists(layout.ExecutablePath) && File.Exists(layout.MarkerPath)
                && !IsSymbolicLink(layout.Root) && !IsSymbolicLink(layout.ExecutablePath)
                && !IsSymbolicLink(layout.MarkerPath) && File.ReadAllText(layout.MarkerPath) == ManagedMarkerContent;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IReadOnlyList<string> ManagedArguments(ManagedLayout layout, IReadOnlyList<string> operation) => ["-p", layout.Root, "-c", layout.ConfigurationPath, .. operation];

    private static WebServerOperationDto Rejected(string instanceId, string kind, string problemCode) =>
        new(Guid.Empty, instanceId, kind, WebServerOperationState.Failed, "validation", problemCode, null, null, DateTimeOffset.UtcNow);

    private sealed record ManagedLayout(string Root, string ExecutablePath, string ConfigurationPath, string MarkerPath, string InstanceId);

    private static IEnumerable<string> FindExternalExecutables()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nginx", "nginx.exe"), @"C:\nginx\nginx.exe" }
            : new[] { "/usr/sbin/nginx", "/usr/bin/nginx", "/usr/local/sbin/nginx", "/usr/local/bin/nginx", "/usr/local/nginx/sbin/nginx", "/usr/local/openresty/nginx/sbin/nginx" };
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string InstanceId(string executable) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable))))[..32].ToLowerInvariant();

    private static string? ParseConfigPath(string output, string executable)
    {
        var match = ConfigurationPathPattern().Match(output);
        if (!match.Success) return null;
        var value = match.Groups["path"].Value.Trim('"', '\'');
        return Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable)!, value));
    }

    private static string? FindOwnedIncludeDirectory(string configPath)
    {
        if (!File.Exists(configPath) || IsSymbolicLink(configPath)) return null;
        try
        {
            var config = File.ReadAllText(configPath);
            var depth = 0;
            var httpDepth = -1;
            foreach (var rawLine in config.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (httpDepth < 0 && HttpBlockPattern().IsMatch(line))
                {
                    depth += line.Count(character => character == '{') - line.Count(character => character == '}');
                    httpDepth = depth;
                    continue;
                }
                if (httpDepth >= 0 && depth == httpDepth)
                {
                    var match = IncludePattern().Match(line);
                    if (match.Success)
                    {
                        var value = match.Groups["path"].Value.Trim().Trim('"', '\'');
                        var directory = Path.GetDirectoryName(value);
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            if (!Path.IsPathFullyQualified(directory)) directory = Path.Combine(Path.GetDirectoryName(configPath)!, directory);
                            directory = Path.GetFullPath(directory);
                            if (Path.GetFileName(directory).Equals("conf.d", StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) return directory;
                        }
                    }
                }
                depth += line.Count(character => character == '{') - line.Count(character => character == '}');
                if (httpDepth >= 0 && depth < httpDepth) httpDepth = -1;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsOwnedFile(string path)
    {
        try { return File.Exists(path) && !IsSymbolicLink(path) && File.ReadAllText(path) == OwnedContent; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool DeleteOwnedFile(string path)
    {
        if (!IsOwnedFile(path)) return false;
        File.Delete(path);
        return true;
    }

    private static bool IsSymbolicLink(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) : false; }
        catch (IOException) { return true; }
    }

    private static bool IsNginxRunning()
    {
        try { return Process.GetProcessesByName("nginx").Length > 0; }
        catch { return false; }
    }

    private static async Task<CommandResult> RunNginxAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.WaitForExitAsync(cancellationToken);
            return new CommandResult(process.ExitCode == 0, (await output) + (await error));
        }
        catch (OperationCanceledException) { throw; }
        catch { return new CommandResult(false, ""); }
    }

    [GeneratedRegex("--conf-path=(?:\\\"(?<path>[^\\\"]+)\\\"|(?<path>[^\\s]+))", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationPathPattern();
    [GeneratedRegex("nginx/(?<version>[^\\s]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^1\\.(?:[0-9]{1,3})\\.(?:[0-9]{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsVersionPattern();
    [GeneratedRegex("nginx/Windows[- ](?<version>1\\.[0-9]{1,3}\\.[0-9]{1,3})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsDownloadVersionPattern();
    [GeneratedRegex("^[0-9][0-9A-Za-z.+:~\\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxPackageVersionPattern();
    [GeneratedRegex("^\\s*include\\s+(?<path>[^;]+\\.conf)\\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IncludePattern();
    [GeneratedRegex("^\\s*http\\s*\\{", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpBlockPattern();

    private sealed record CommandResult(bool Success, string Output);
}
