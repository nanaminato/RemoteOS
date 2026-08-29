using System.Collections.Concurrent;
using RemoteOS.Protocol.Registry;
using Server.Domain;

namespace Server.Storage;

public interface IRegistryRepository
{
    IReadOnlyList<RegistryEntry> List(Guid userId, RegistryScope? scope = null);
    RegistryEntry? Find(Guid userId, RegistryScope scope, Guid scopeId, string path, string name);
    void SeedSynced(RegistryEntry entry);
}

/// <summary>Development fallback with the same tenant key as SQLite.</summary>
public sealed class InMemoryRegistryRepository : IRegistryRepository
{
    private readonly ConcurrentDictionary<(Guid, RegistryScope, Guid, string, string), RegistryEntry> _entries = new();
    public IReadOnlyList<RegistryEntry> List(Guid userId, RegistryScope? scope = null) => _entries.Values
        .Where(x => x.UserId == userId && (scope is null || x.Scope == scope))
        .OrderBy(x => x.Scope).ThenBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).Select(Copy).ToArray();
    public RegistryEntry? Find(Guid userId, RegistryScope scope, Guid scopeId, string path, string name) =>
        _entries.TryGetValue((userId, scope, scopeId, path, name), out var entry) ? Copy(entry) : null;
    public void SeedSynced(RegistryEntry entry) => _entries.TryAdd(Key(entry), Copy(entry));
    private static (Guid, RegistryScope, Guid, string, string) Key(RegistryEntry x) => (x.UserId, x.Scope, x.ScopeId, x.Path, x.Name);
    private static RegistryEntry Copy(RegistryEntry x) => new() { UserId = x.UserId, Scope = x.Scope, ScopeId = x.ScopeId, Path = x.Path, Name = x.Name, ValueType = x.ValueType, ValueJson = x.ValueJson, Revision = x.Revision, State = x.State, DesiredUpdatedAt = x.DesiredUpdatedAt, DesiredUpdatedBy = x.DesiredUpdatedBy, AppliedRevision = x.AppliedRevision, AppliedAt = x.AppliedAt, LastErrorCode = x.LastErrorCode, LastErrorMessage = x.LastErrorMessage };
}
