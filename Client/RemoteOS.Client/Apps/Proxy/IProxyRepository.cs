using RemoteOS.Protocol.Proxy;

namespace Client.Apps.Proxy;

public interface IProxyRepository
{
    Task<ProxyOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyGroupDto>> ListGroupsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyConnectionDto>> ListConnectionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyLogEntryDto>> ListLogsAsync(int limit = 200, CancellationToken cancellationToken = default);
    Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken = default);
    Task<ProxyRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default);
    Task<ProxyOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> LifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> InstallRuntimeAsync(string engineId, string? version = null, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> InstallRuntimeFromServerFileAsync(string engineId, string archivePath, string? version = null, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> RollbackRuntimeAsync(CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> UninstallRuntimeAsync(CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> EnableTunAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> DisableTunAsync(CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> EmergencyDisableTunAsync(CancellationToken cancellationToken = default);
    Task<ProxyProfileDto> CreateProfileAsync(string name, string engineId, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<ProxyProfileDto> ActivateProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SelectGroupAsync(string groupName, string proxy, CancellationToken cancellationToken = default);
    Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default);
}
