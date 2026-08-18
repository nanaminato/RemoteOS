using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    NginxManagedOptions managedOptions) : IWebServerProvider
{
    private const string ProviderKey = "nginx";
    private const string OwnedFileName = "remoteos.conf";
    private const string OwnershipMarker = "# Managed by RemoteOS. Do not edit.";
    private const string ManagedMarkerName = ".remoteos-managed";
    private const string ManagedMarkerContent = "RemoteOS owns this Nginx installation. Do not move this marker.\n";
    private static readonly string OwnedContent = $"{OwnershipMarker}\n# RemoteOS-owned Nginx integration anchor.\n";
    private static readonly SemaphoreSlim IntegrationGate = new(1, 1);

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
        var result = await RunNginxAsync(detected.ExecutablePath, ["-t"], cancellationToken);
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
        if (!IsConfiguredInstaller())
            return Rejected(layout.InstanceId, "install", "webserver.install_not_configured");
        return await operations.StartAsync(idempotencyKey, layout.InstanceId, "install", actor,
            ct => InstallManagedCoreAsync(layout, ct), lifetime.ApplicationStopping);
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
        var configPath = ParseConfigPath(details.Output, executable);
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

    private async Task<WebServerOperationResult> InstallManagedCoreAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        var started = await RunConfiguredInstallerAsync(cancellationToken);
        if (!started) return new WebServerOperationResult("webserver.install_failed");
        if (!File.Exists(layout.ExecutablePath) || IsSymbolicLink(layout.ExecutablePath) || !Directory.Exists(layout.Root) || IsSymbolicLink(layout.Root))
            return new WebServerOperationResult("webserver.install_layout_invalid");
        try
        {
            await File.WriteAllTextAsync(layout.MarkerPath, ManagedMarkerContent, new UTF8Encoding(false), cancellationToken);
            var test = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);
            if (test.Success) return new WebServerOperationResult("");
            File.Delete(layout.MarkerPath);
            return new WebServerOperationResult("webserver.config_test_failed");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.install_layout_invalid"); }
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

    private async Task<bool> RunConfiguredInstallerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(managedOptions.InstallerCommand) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
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
    [GeneratedRegex("^\\s*include\\s+(?<path>[^;]+\\.conf)\\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IncludePattern();
    [GeneratedRegex("^\\s*http\\s*\\{", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpBlockPattern();

    private sealed record CommandResult(bool Success, string Output);
}
