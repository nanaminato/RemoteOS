using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Registry;
using Server.Domain;
using Server.Storage.Sqlite;

namespace Server.ConfigurationRegistry;

/// <summary>Imports pre-registry workspace configuration once, preserving it as synchronized desired state.</summary>
public static class RegistryBootstrapper
{
    private const string DefaultValueName = "(Default)";

    public static void ImportWorkspaceConfiguration(RemoteOsDbContext db)
    {
        db.Database.ExecuteSqlRaw("UPDATE registry_entries SET Name = '(Default)' WHERE Name = 'Settings' AND Path IN ('Workspace\\Terminal\\Appearance', 'Workspace\\Desktop\\Preferences', 'Workspace\\Browser\\Settings');");
        // Browser settings are a value of the Browser key, not a synthetic Settings subkey.
        // Preserve an already migrated value if both paths exist.
        db.Database.ExecuteSqlRaw("DELETE FROM registry_entries WHERE Path = 'Workspace\\Browser\\Settings' AND EXISTS (SELECT 1 FROM registry_entries migrated WHERE migrated.UserId = registry_entries.UserId AND migrated.Scope = registry_entries.Scope AND migrated.ScopeId = registry_entries.ScopeId AND migrated.Path = 'Workspace\\Browser' AND migrated.Name = registry_entries.Name);");
        db.Database.ExecuteSqlRaw("UPDATE registry_entries SET Path = 'Workspace\\Browser' WHERE Path = 'Workspace\\Browser\\Settings';");
        foreach (var workspace in db.Workspaces)
        {
            Seed(db, workspace.UserId, workspace.Id, "Workspace\\Terminal\\Appearance", workspace.TerminalSettings);
            Seed(db, workspace.UserId, workspace.Id, "Workspace\\Desktop\\Preferences", workspace.Preferences);
            Seed(db, workspace.UserId, workspace.Id, "Workspace\\Browser", workspace.BrowserSettings);
        }
        db.SaveChanges();
    }

    private static void Seed(RemoteOsDbContext db, Guid userId, Guid workspaceId, string path, object value)
    {
        if (db.RegistryEntries.Any(x => x.UserId == userId && x.Scope == RegistryScope.Workspace && x.ScopeId == workspaceId && x.Path == path && x.Name == DefaultValueName)) return;
        var now = DateTimeOffset.UtcNow;
        db.RegistryEntries.Add(new RegistryEntry
        {
            UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId, Path = path, Name = DefaultValueName,
            ValueType = RegistryValueType.Json, ValueJson = JsonSerializer.Serialize(value, RemoteOsJsonOptions.Default), Revision = 1,
            State = RegistryEntryState.Synced, DesiredUpdatedAt = now, DesiredUpdatedBy = "migration",
            AppliedRevision = 1, AppliedAt = now,
        });
        db.RegistryKeys.Add(new RegistryKey
        {
            UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId, Path = path,
            CreatedAt = now, CreatedBy = "migration",
        });
    }
}
