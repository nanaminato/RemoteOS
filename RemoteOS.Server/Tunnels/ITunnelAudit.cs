using RemoteOS.Protocol.Tunnels;

namespace Server.Tunnels;

public interface ITunnelAudit
{
    Task RecordAsync(string actorUserId, string action, Guid? targetId, string result, string? problemCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<TunnelAuditEntryDto>> ListFrpsAsync(CancellationToken cancellationToken);
}
