using RemoteOS.Protocol.Tunnels;

namespace Server.Runtimes;

public interface IRuntimeManager
{
    Task<TunnelRuntimeDto> DetectExternalFrpcAsync(string executablePath, CancellationToken cancellationToken);
    Task<TunnelRuntimeDto> GetManagedFrpcStatusAsync(CancellationToken cancellationToken);
    TunnelRuntimeInstallationDto GetManagedFrpcInstallationStatus();
    Task<TunnelOperationResultDto> InstallManagedFrpcAsync(string version, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> InstallManagedFrpcFromArchiveAsync(string version, string archivePath, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> RollbackManagedFrpcAsync(CancellationToken cancellationToken);
}
