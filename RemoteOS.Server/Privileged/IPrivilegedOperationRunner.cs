using RemoteOS.Protocol.Privileged;

namespace Server.Privileged;

public interface IPrivilegedOperationRunner
{
    Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default);
}
