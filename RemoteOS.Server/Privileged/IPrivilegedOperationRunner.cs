using RemoteOS.Protocol.Privileged;

namespace Server.Privileged;

/// <summary>Compatibility name for older consumers. New code depends on <see cref="IPrivilegedOperationTransport"/>.</summary>
public interface IPrivilegedOperationRunner : IPrivilegedOperationTransport
{
}
