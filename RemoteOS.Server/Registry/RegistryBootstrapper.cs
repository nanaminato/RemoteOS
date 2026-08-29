using System.Text.Json;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Registry;
using Server.Domain;
using Server.Storage.Sqlite;

namespace Server.ConfigurationRegistry;

/// <summary>Imports pre-registry workspace configuration once, preserving it as synchronized desired state.</summary>
public static class RegistryBootstrapper
{
    public static void ImportWorkspaceConfiguration(RemoteOsDbContext db)
    {
        foreach (var workspace in db.Workspaces)
        {
            Seed(db, workspace.UserId, workspace.Id, "Workspace\\Terminal\\Appearance", workspace.TerminalSettings);
            Seed(db, workspace.UserId, workspace.Id, "Workspace\\Desktop\\Preferences", workspace.Preferences);
            Seed(db, workspace.UserId, workspace.Id, "Workspace\\Browser\\Settings", workspace.BrowserSettings);
        }
        db.SaveChanges();
    }

    private static void Seed(RemoteOsDbContext db, Guid userId, Guid workspaceId, string path, object value)
    {
        if (db.RegistryEntries.Any(x => x.UserId == userId && x.Scope == RegistryScope.Workspace && x.ScopeId == workspaceId && x.Path == path && x.Name == "Settings")) return;
        var now = DateTimeOffset.UtcNow;
        db.RegistryEntries.Add(new RegistryEntry
        {
            UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId, Path = path, Name = "Settings",
            ValueType = RegistryValueType.Json, ValueJson = JsonSerializer.Serialize(value, RemoteOsJsonOptions.Default), Revision = 1,
            State = RegistryEntryState.Synced, DesiredUpdatedAt = now, DesiredUpdatedBy = "migration",
            AppliedRevision = 1, AppliedAt = now,
        });
    }
}
