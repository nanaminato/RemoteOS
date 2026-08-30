using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RemoteOS.Protocol.Registry;
using Server.Domain;
using Server.Storage;
using Server.Storage.Sqlite;

namespace Server.ConfigurationRegistry;

/// <summary>
/// The configuration registry's authoritative runtime copy. Reads and mutations are served
/// from memory; SQLite is a durable, delayed write-behind copy used for restart recovery.
/// </summary>
public sealed class CachedSqliteRegistryRepository(IDbContextFactory<RemoteOsDbContext> dbFactory) : IRegistryRepository, IHostedService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<EntryKey, RegistryEntry> _entries = new();
    private readonly HashSet<EntryKey> _dirty = [];
    private readonly HashSet<EntryKey> _deleted = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _flushLoop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entries = await db.RegistryEntries.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var entry in entries)
            _entries[EntryKey.From(entry)] = Copy(entry);

        _flushLoop = FlushLoopAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_flushLoop is not null)
        {
            try { await _flushLoop; }
            catch (OperationCanceledException) { }
        }
        await FlushAsync(cancellationToken);
        _stopping.Dispose();
    }

    public IReadOnlyList<RegistryEntry> List(Guid userId, RegistryScope? scope = null) => _entries.Values
        .Where(x => x.UserId == userId && (scope is null || x.Scope == scope))
        .OrderBy(x => x.Scope).ThenBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)
        .Select(Copy).ToArray();

    public RegistryEntry? Find(Guid userId, RegistryScope scope, Guid scopeId, string path, string name) =>
        _entries.TryGetValue(new EntryKey(userId, scope, scopeId, path, name), out var entry) ? Copy(entry) : null;

    public RegistryEntry Upsert(RegistryEntry entry)
    {
        var key = EntryKey.From(entry);
        lock (_gate)
        {
            var saved = Copy(entry);
            saved.Revision = _entries.TryGetValue(key, out var current) ? current.Revision + 1 : Math.Max(1, saved.Revision);
            // PendingSync makes the delayed-persistence state explicit to API consumers.
            saved.State = RegistryEntryState.PendingSync;
            saved.AppliedRevision = null;
            saved.AppliedAt = null;
            saved.LastErrorCode = null;
            saved.LastErrorMessage = null;
            _entries[key] = saved;
            _deleted.Remove(key);
            _dirty.Add(key);
            return Copy(saved);
        }
    }

    public bool Delete(Guid userId, RegistryScope scope, Guid scopeId, string path, string name)
    {
        var key = new EntryKey(userId, scope, scopeId, path, name);
        lock (_gate)
        {
            if (!_entries.TryRemove(key, out _)) return false;
            _dirty.Remove(key);
            _deleted.Add(key);
            return true;
        }
    }

    public void SeedSynced(RegistryEntry entry)
    {
        var key = EntryKey.From(entry);
        _entries.TryAdd(key, Copy(entry));
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        Dictionary<EntryKey, RegistryEntry> dirty;
        EntryKey[] deleted;
        lock (_gate)
        {
            if (_dirty.Count == 0 && _deleted.Count == 0) return;
            dirty = _dirty
                .Where(key => _entries.TryGetValue(key, out _))
                .ToDictionary(key => key, key => Copy(_entries[key]));
            deleted = _deleted.ToArray();
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            foreach (var entry in dirty.Values)
            {
                var existing = await db.RegistryEntries.FindAsync(
                    [entry.UserId, entry.Scope, entry.ScopeId, entry.Path, entry.Name], cancellationToken);
                if (existing is null)
                {
                    var durable = Copy(entry);
                    durable.State = RegistryEntryState.Synced;
                    durable.AppliedRevision = durable.Revision;
                    durable.AppliedAt = DateTimeOffset.UtcNow;
                    db.RegistryEntries.Add(durable);
                }
                else
                    CopyInto(entry, existing);
            }
            foreach (var key in deleted)
            {
                var existing = await db.RegistryEntries.FindAsync(
                    [key.UserId, key.Scope, key.ScopeId, key.Path, key.Name], cancellationToken);
                if (existing is not null) db.RegistryEntries.Remove(existing);
            }
            await db.SaveChangesAsync(cancellationToken);

            lock (_gate)
            {
                foreach (var (key, flushed) in dirty)
                {
                    if (_entries.TryGetValue(key, out var current) && current.Revision == flushed.Revision)
                    {
                        current.State = RegistryEntryState.Synced;
                        current.AppliedRevision = current.Revision;
                        current.AppliedAt = DateTimeOffset.UtcNow;
                        _dirty.Remove(key);
                    }
                }
                foreach (var key in deleted)
                {
                    // A later upsert wins over the delete snapshot and remains dirty.
                    if (!_entries.ContainsKey(key)) _deleted.Remove(key);
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Keep the dirty snapshot in memory for the next scheduled retry. The runtime
            // registry remains usable even during a transient database outage.
        }
    }

    private static RegistryEntry Copy(RegistryEntry x) => new()
    {
        UserId = x.UserId, Scope = x.Scope, ScopeId = x.ScopeId, Path = x.Path, Name = x.Name,
        ValueType = x.ValueType, ValueJson = x.ValueJson, Revision = x.Revision, State = x.State,
        DesiredUpdatedAt = x.DesiredUpdatedAt, DesiredUpdatedBy = x.DesiredUpdatedBy,
        AppliedRevision = x.AppliedRevision, AppliedAt = x.AppliedAt,
        LastErrorCode = x.LastErrorCode, LastErrorMessage = x.LastErrorMessage,
    };

    private static void CopyInto(RegistryEntry source, RegistryEntry target)
    {
        target.ValueType = source.ValueType;
        target.ValueJson = source.ValueJson;
        target.Revision = source.Revision;
        target.State = RegistryEntryState.Synced;
        target.DesiredUpdatedAt = source.DesiredUpdatedAt;
        target.DesiredUpdatedBy = source.DesiredUpdatedBy;
        target.AppliedRevision = source.Revision;
        target.AppliedAt = DateTimeOffset.UtcNow;
        target.LastErrorCode = null;
        target.LastErrorMessage = null;
    }

    private readonly record struct EntryKey(Guid UserId, RegistryScope Scope, Guid ScopeId, string Path, string Name)
    {
        public static EntryKey From(RegistryEntry entry) => new(entry.UserId, entry.Scope, entry.ScopeId, entry.Path, entry.Name);
    }
}
