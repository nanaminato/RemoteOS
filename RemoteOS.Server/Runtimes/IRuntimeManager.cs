using RemoteOS.Protocol.Tunnels;

namespace Server.Runtimes;

public interface IRuntimeManager
{
    Task<TunnelRuntimeDto> DetectExternalFrpcAsync(string executablePath, CancellationToken cancellationToken);
    Task<TunnelRuntimeDto> GetManagedFrpcStatusAsync(CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> InstallManagedFrpcAsync(string version, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> RollbackManagedFrpcAsync(CancellationToken cancellationToken);
}
