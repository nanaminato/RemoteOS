using Microsoft.EntityFrameworkCore;
using RemoteOS.Protocol.Registry;
using Server.Domain;

namespace Server.Storage.Sqlite;

public sealed class SqliteRegistryRepository(RemoteOsDbContext db) : IRegistryRepository
{
    public IReadOnlyList<RegistryEntry> List(Guid userId, RegistryScope? scope = null)
    {
        var query = db.RegistryEntries.AsNoTracking().Where(x => x.UserId == userId);
        if (scope is not null) query = query.Where(x => x.Scope == scope.Value);
        return query.OrderBy(x => x.Scope).ThenBy(x => x.Path).ThenBy(x => x.Name).ToArray();
    }
    public RegistryEntry? Find(Guid userId, RegistryScope scope, Guid scopeId, string path, string name) =>
        db.RegistryEntries.Find(userId, scope, scopeId, path, name);
    public void SeedSynced(RegistryEntry entry)
    {
        if (Find(entry.UserId, entry.Scope, entry.ScopeId, entry.Path, entry.Name) is not null) return;
        db.RegistryEntries.Add(entry);
        db.SaveChanges();
    }
}
