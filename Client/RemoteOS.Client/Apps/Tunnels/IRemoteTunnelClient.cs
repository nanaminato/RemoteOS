using RemoteOS.Protocol.Tunnels;

namespace Client.Apps.Tunnels;

public interface IRemoteTunnelClient
{
    Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TunnelDefinitionDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<TunnelRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> ApplyAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> StopAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> InstallManagedRuntimeAsync(string version, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> RollbackManagedRuntimeAsync(CancellationToken cancellationToken = default);
}
