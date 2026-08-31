using RemoteOS.Protocol.Proxy;

namespace Server.Proxy;

/// <summary>Engine-neutral Server boundary. Concrete controller schemas never cross it.</summary>
public interface IProxyEngine
{
    string EngineId { get; }
    Task<ProxyEngineCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<ProxyHealthDto> GetHealthAsync(CancellationToken cancellationToken);
    Task<string?> ValidateConfigurationAsync(string configurationPath, CancellationToken cancellationToken);
    Task<string?> ReloadAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProxyGroupDto>> GetGroupsAsync(CancellationToken cancellationToken);
    Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProxyConnectionDto>> GetConnectionsAsync(CancellationToken cancellationToken);
    Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProxyLogEntryDto>> GetLogsAsync(int limit, CancellationToken cancellationToken);
    Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken);
}

public interface IProxyEngineRegistry
{
    IProxyEngine? Find(string engineId);
    IReadOnlyList<IProxyEngine> List();
}

public interface IProxyRuntimeManager
{
    Task<ProxyRuntimeDto> GetAsync(string engineId, CancellationToken cancellationToken);
    Task<ProxyRuntimeDto> DetectExternalAsync(string engineId, string executablePath, CancellationToken cancellationToken);
    Task<ProxyRuntimeDto> InstallManagedAsync(string engineId, string? version, CancellationToken cancellationToken);
    Task<ProxyRuntimeDto> RollbackManagedAsync(string engineId, CancellationToken cancellationToken);
    Task<ProxyRuntimeDto> UninstallManagedAsync(string engineId, CancellationToken cancellationToken);
}

public interface IProxyProfileService
{
    Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken cancellationToken);
    Task<ProxyProfileDto?> GetAsync(Guid profileId, CancellationToken cancellationToken);
}

public interface IProxyConfigurationService
{
    Task<string?> ValidateAsync(Guid profileId, CancellationToken cancellationToken);
}

public interface IProxyRecoveryService
{
    Task<ProxyRecoveryStatusDto> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>Server-only during Goal 5. No Endpoint or Client may invoke this boundary yet.</summary>
public interface IProxyTunSafetyService : IProxyRecoveryService
{
    Task<string?> EnableAsync(Guid profileId, CancellationToken cancellationToken);
    Task<string?> DisableAsync(CancellationToken cancellationToken);
    Task<string?> EmergencyDisableAsync(CancellationToken cancellationToken);
    Task<string?> EvaluateRecoveryAsync(CancellationToken cancellationToken);
}

/// <summary>Platform boundary only; it does not expose commands, passwords, or arbitrary paths.</summary>
public interface IProxyPlatformService
{
    Task<ProxyPlatformCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
}

public interface IProxyPlatformPaths
{
    string GetEngineVersionsDirectory(string engineId);
    string GetProtectedConfigurationDirectory();
    string GetStateDirectory();
    string GetSanitizedLogDirectory();
}
