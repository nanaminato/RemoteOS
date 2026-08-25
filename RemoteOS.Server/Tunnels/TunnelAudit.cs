using Server.Domain;
using Server.Storage.Sqlite;

namespace Server.Tunnels;

public sealed class TunnelAudit(RemoteOsDbContext db) : ITunnelAudit
{
    public async Task RecordAsync(string actorUserId, string action, Guid? targetId, string result, string? problemCode, CancellationToken ct)
    {
        db.Set<TunnelAuditEntry>().Add(new TunnelAuditEntry { Id = Guid.NewGuid(), ActorUserId = actorUserId, Action = action, TargetId = targetId, Result = result, ProblemCode = problemCode, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
    }
}
