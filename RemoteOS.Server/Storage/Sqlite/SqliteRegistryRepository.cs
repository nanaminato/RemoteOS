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
    public RegistryEntry Upsert(RegistryEntry entry)
    {
        var existing = Find(entry.UserId, entry.Scope, entry.ScopeId, entry.Path, entry.Name);
        if (existing is null) db.RegistryEntries.Add(entry);
        else
        {
            existing.ValueType = entry.ValueType;
            existing.ValueJson = entry.ValueJson;
            existing.Revision++;
            existing.State = entry.State;
            existing.DesiredUpdatedAt = entry.DesiredUpdatedAt;
            existing.DesiredUpdatedBy = entry.DesiredUpdatedBy;
            existing.AppliedRevision = entry.AppliedRevision;
            existing.AppliedAt = entry.AppliedAt;
            existing.LastErrorCode = entry.LastErrorCode;
            existing.LastErrorMessage = entry.LastErrorMessage;
            entry = existing;
        }
        db.SaveChanges();
        return entry;
    }
    public bool Delete(Guid userId, RegistryScope scope, Guid scopeId, string path, string name)
    {
        var entry = Find(userId, scope, scopeId, path, name);
        if (entry is null) return false;
        db.RegistryEntries.Remove(entry);
        db.SaveChanges();
        return true;
    }
    public void SeedSynced(RegistryEntry entry)
    {
        if (Find(entry.UserId, entry.Scope, entry.ScopeId, entry.Path, entry.Name) is not null) return;
        db.RegistryEntries.Add(entry);
        db.SaveChanges();
    }
    public IReadOnlyList<RegistryKey> ListChildKeys(Guid userId, RegistryScope scope, Guid scopeId, string parentPath)
    {
        var prefix = parentPath + "\\";
        return db.RegistryKeys.AsNoTracking()
            .Where(x => x.UserId == userId && x.Scope == scope && x.ScopeId == scopeId && x.Path.StartsWith(prefix))
            .AsEnumerable().Where(x => x.Path[(prefix.Length)..].IndexOf('\\') < 0).OrderBy(x => x.Path).ToArray();
    }
    public RegistryKey CreateKey(RegistryKey key)
    {
        var existing = db.RegistryKeys.Find(key.UserId, key.Scope, key.ScopeId, key.Path);
        if (existing is not null) return existing;
        db.RegistryKeys.Add(key);
        db.SaveChanges();
        return key;
    }
    public bool DeleteKeyTree(Guid userId, RegistryScope scope, Guid scopeId, string path)
    {
        var prefix = path + "\\";
        var keys = db.RegistryKeys.Where(x => x.UserId == userId && x.Scope == scope && x.ScopeId == scopeId && (x.Path == path || x.Path.StartsWith(prefix))).ToArray();
        var entries = db.RegistryEntries.Where(x => x.UserId == userId && x.Scope == scope && x.ScopeId == scopeId && (x.Path == path || x.Path.StartsWith(prefix))).ToArray();
        if (keys.Length == 0 && entries.Length == 0) return false;
        db.RegistryKeys.RemoveRange(keys);
        db.RegistryEntries.RemoveRange(entries);
        db.SaveChanges();
        return true;
    }
}
