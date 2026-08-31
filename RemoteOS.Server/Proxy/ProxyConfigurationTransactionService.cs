using RemoteOS.Protocol.Proxy;

namespace Server.Proxy;

/// <summary>Serializes raw-YAML application. Raw configuration is never returned through public contracts.</summary>
public interface IProxyConfigurationTransactionService
{
    Task<string?> ApplyAsync(Guid profileId, string yaml, CancellationToken cancellationToken);
}

public sealed class ProxyConfigurationTransactionService(
    IProxyPlatformPaths paths,
    IProxyEngineRegistry engines,
    IProxyProfileRepository profiles) : IProxyConfigurationTransactionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<string?> ApplyAsync(Guid profileId, string yaml, CancellationToken cancellationToken)
    {
        if (yaml.Length is 0 or > 1_048_576 || yaml.IndexOf('\0') >= 0) return ProxyProblemCodes.ConfigInvalid;
        var profile = await profiles.GetAsync(profileId, cancellationToken);
        var engine = profile is null ? null : engines.Find(profile.EngineId);
        if (engine is null) return ProxyProblemCodes.NotSupported;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = paths.GetProtectedConfigurationDirectory(); Directory.CreateDirectory(directory); SetPrivateDirectory(directory);
            var active = Path.Combine(directory, "active.yaml");
            var temporary = Path.Combine(directory, ".apply-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(directory, "backup-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".yaml");
            try
            {
                await File.WriteAllTextAsync(temporary, yaml, cancellationToken); SetPrivateFile(temporary);
                var validation = await engine.ValidateConfigurationAsync(temporary, cancellationToken);
                if (!string.IsNullOrEmpty(validation)) { File.Delete(temporary); return validation; }
                if (File.Exists(active)) File.Copy(active, backup, overwrite: false);
                File.Move(temporary, active, overwrite: true); SetPrivateFile(active);
                var reload = await engine.ReloadAsync(cancellationToken);
                var health = string.IsNullOrEmpty(reload) ? await engine.GetHealthAsync(cancellationToken) : null;
                if (string.IsNullOrEmpty(reload) && health?.State == ProxyHealthState.Healthy) return null;
                if (File.Exists(backup))
                {
                    File.Copy(backup, active, overwrite: true); SetPrivateFile(active);
                    var rollback = await engine.ReloadAsync(cancellationToken);
                    var rollbackHealth = string.IsNullOrEmpty(rollback) ? await engine.GetHealthAsync(cancellationToken) : null;
                    return rollbackHealth?.State == ProxyHealthState.Healthy ? ProxyProblemCodes.ConfigApplyFailed : ProxyProblemCodes.RecoveryRequired;
                }
                return ProxyProblemCodes.ConfigApplyFailed;
            }
            catch (IOException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
    private static void SetPrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void SetPrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
}

public sealed class ProxyProfileService(IProxyProfileRepository profiles) : IProxyProfileService
{
    public Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken cancellationToken) => profiles.ListAsync(cancellationToken);
    public Task<ProxyProfileDto?> GetAsync(Guid profileId, CancellationToken cancellationToken) => profiles.GetAsync(profileId, cancellationToken);
}

public sealed class ProxyConfigurationService : IProxyConfigurationService
{
    // Public configuration validation/apply endpoints are introduced only in Goal 6.
    public Task<string?> ValidateAsync(Guid profileId, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
}
