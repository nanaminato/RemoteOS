using RemoteOS.Protocol.Tunnels;

namespace Client.Apps.Tunnels;

public interface IRemoteTunnelClient
{
    Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TunnelDefinitionDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<TunnelRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default);
    Task<TunnelRuntimeInstallationDto> GetRuntimeInstallationStatusAsync(CancellationToken cancellationToken = default);
    Task<TunnelServerProfileDto> CreateProfileAsync(UpsertTunnelServerProfileRequest request, CancellationToken cancellationToken = default);
    Task<TunnelServerProfileDto> UpdateProfileAsync(Guid profileId, UpsertTunnelServerProfileRequest request, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SetProfileTokenAsync(Guid profileId, string token, CancellationToken cancellationToken = default);
    Task<TunnelDefinitionDto> CreateTunnelAsync(UpsertTunnelDefinitionRequest request, CancellationToken cancellationToken = default);
    Task<TunnelDefinitionDto> UpdateTunnelAsync(Guid tunnelId, UpsertTunnelDefinitionRequest request, CancellationToken cancellationToken = default);
    Task DeleteTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> ApplyAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> StopAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> InstallManagedRuntimeAsync(string version, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> InstallManagedRuntimeFromServerFileAsync(string version, string archivePath, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> RollbackManagedRuntimeAsync(CancellationToken cancellationToken = default);
    Task<TunnelRuntimeDto> DetectExternalRuntimeAsync(string executablePath, CancellationToken cancellationToken = default);
}
