using RemoteOS.Protocol.Privileged;

namespace Server.ProcessGuardian;

/// <summary>Capability-specific service control boundary; it is not a general process runner.</summary>
public interface IPrivilegedNativeServiceOperations
{
    Task<bool> ApplyAsync(string serviceId, PrivilegedServiceAction action, CancellationToken cancellationToken = default);
}

public sealed class PrivilegedNativeServiceOperations(Server.Privileged.IPrivilegedOperationTransport transport) : IPrivilegedNativeServiceOperations
{
    public async Task<bool> ApplyAsync(string serviceId, PrivilegedServiceAction action, CancellationToken cancellationToken = default)
        => (await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NativeServiceAction,
            ServiceId: serviceId, ServiceAction: action), cancellationToken)).Success;
}
