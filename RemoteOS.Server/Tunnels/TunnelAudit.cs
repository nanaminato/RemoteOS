using Microsoft.EntityFrameworkCore;
using RemoteOS.Protocol.Tunnels;
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

    public async Task<IReadOnlyList<TunnelAuditEntryDto>> ListFrpsAsync(CancellationToken ct)
    {
        // Microsoft.EntityFrameworkCore.Sqlite cannot translate ordering by DateTimeOffset.
        // Keep filtering in SQL, then apply the bounded, presentation-only ordering in memory.
        var entries = await db.TunnelAuditEntries.AsNoTracking()
            .Where(x => x.Action.StartsWith("frps."))
            .ToListAsync(ct);
        return entries.OrderByDescending(x => x.CreatedAt).Take(200)
            .Select(x => new TunnelAuditEntryDto(x.CreatedAt, x.Action, x.Result, x.ProblemCode ?? "")).ToArray();
    }
}
