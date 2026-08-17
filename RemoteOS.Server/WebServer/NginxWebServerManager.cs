using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RemoteOS.Protocol.WebServers;

namespace Server.WebServer;

/// <summary>Nginx V1 provider. It uses fixed executable arguments and only creates one owned,
/// marker-bearing file in an already-included conf.d directory.</summary>
internal sealed partial class NginxWebServerManager(
    IHostPrivilegeService privileges,
    WebServerOperationStore operations,
    WebServerMetadataRepository metadata,
    IHostApplicationLifetime lifetime) : IWebServerManager
{
    private const string ProviderId = "nginx";
    private const string OwnedFileName = "remoteos.conf";
    private const string OwnershipMarker = "# Managed by RemoteOS. Do not edit.";
    private static readonly string OwnedContent = $"{OwnershipMarker}\n# RemoteOS-owned Nginx integration anchor.\n";
    private static readonly SemaphoreSlim IntegrationGate = new(1, 1);

    public async Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var instance = await DetectAsync(cancellationToken);
        return instance is null ? [] : [instance];
    }

    public Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken) => DiscoverAsync(cancellationToken);

    public async Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = await DetectAsync(cancellationToken);
        if (instance is null || instance.Id != instanceId) return null;
        var running = IsNginxRunning();
        return new WebServerStatusDto(instanceId, running ? WebServerRuntimeState.Running : WebServerRuntimeState.Stopped);
    }

    public async Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken)
    {
        var detected = await DetectAsync(cancellationToken);
        if (detected is null || detected.Id != instanceId) return null;
        var result = await RunNginxAsync(detected.ExecutablePath, ["-t"], cancellationToken);
        return new WebServerConfigTestResultDto(result.Success, result.Success ? "" : "webserver.config_test_failed");
    }

    public async Task<WebServerOperationDto?> IntegrateAsync(string instanceId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        var detected = await DetectAsync(cancellationToken);
        if (detected is null || detected.Id != instanceId) return null;
        if (!request.Confirmed)
            return new WebServerOperationDto(Guid.Empty, instanceId, "integrate", WebServerOperationState.Failed, "validation", "webserver.confirmation_required", null, null, DateTimeOffset.UtcNow);
        if (!privileges.IsAdministrator)
            return new WebServerOperationDto(Guid.Empty, instanceId, "integrate", WebServerOperationState.Failed, "authorization", "webserver.config_elevation_required", null, null, DateTimeOffset.UtcNow);
        return await operations.StartAsync(idempotencyKey, instanceId, "integrate", actor, ct => IntegrateCoreAsync(detected, ct), lifetime.ApplicationStopping);
    }

    public async Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        var detected = await DetectAsync(cancellationToken);
        if (detected is null || detected.Id != instanceId) return null;
        if (detected.ManagementMode != WebServerManagementMode.Integrated)
            return new WebServerOperationDto(Guid.Empty, instanceId, "reload", WebServerOperationState.Failed, "authorization", "webserver.reload_not_permitted", null, null, DateTimeOffset.UtcNow);
        if (!privileges.IsAdministrator)
            return new WebServerOperationDto(Guid.Empty, instanceId, "reload", WebServerOperationState.Failed, "authorization", "webserver.lifecycle_elevation_required", null, null, DateTimeOffset.UtcNow);
        return await operations.StartAsync(idempotencyKey, instanceId, "reload", actor, async ct =>
            new WebServerOperationResult((await RunNginxAsync(detected.ExecutablePath, ["-s", "reload"], ct)).Success ? "" : "webserver.reload_failed"), lifetime.ApplicationStopping);
    }

    private async Task<WebServerDto?> DetectAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable();
        if (executable is null) return null;
        var details = await RunNginxAsync(executable, ["-V"], cancellationToken);
        if (!details.Success && string.IsNullOrWhiteSpace(details.Output)) return null;
        var configPath = ParseConfigPath(details.Output, executable);
        var version = VersionPattern().Match(details.Output) is { Success: true } match ? match.Groups["version"].Value : null;
        var includeDirectory = configPath is null ? null : FindOwnedIncludeDirectory(configPath);
        var ownedPath = includeDirectory is null ? null : Path.Combine(includeDirectory, OwnedFileName);
        var integrated = ownedPath is not null && IsOwnedFile(ownedPath);
        var mode = integrated ? WebServerManagementMode.Integrated : WebServerManagementMode.External;
        var capabilities = new WebServerCapabilities(
            CanRead: true,
            CanTestConfiguration: true,
            CanIntegrate: !integrated && privileges.IsAdministrator && includeDirectory is not null,
            CanReload: integrated && privileges.IsAdministrator);
        var instance = new WebServerDto(InstanceId(executable), ProviderId, WebServerType.Nginx, mode, executable, configPath, version, DateTimeOffset.UtcNow, capabilities);
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

    private static string? FindExecutable()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nginx", "nginx.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteOS", "webserver", "nginx", "nginx.exe") }
            : new[] { "/usr/sbin/nginx", "/usr/bin/nginx", "/usr/local/sbin/nginx", "/usr/local/bin/nginx" };
        return candidates.FirstOrDefault(File.Exists);
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
