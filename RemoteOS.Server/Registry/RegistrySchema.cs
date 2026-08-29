using RemoteOS.Protocol.Registry;

namespace Server.ConfigurationRegistry;

/// <summary>Code-owned allow-list. Registry paths are never a generic database editing surface.</summary>
public sealed record RegistrySchemaDefinition(
    RegistryScope Scope, string Path, string Name, RegistryValueType ValueType,
    RegistryApplyMode ApplyMode, string? RestartTarget = null);

public static class RegistrySchema
{
    public static IReadOnlyList<RegistrySchemaDefinition> Definitions { get; } =
    [
        new(RegistryScope.Workspace, "Workspace\\Terminal\\Appearance", "(Default)", RegistryValueType.Json, RegistryApplyMode.RestartApplication, "remoteos.terminal"),
        new(RegistryScope.Workspace, "Workspace\\Desktop\\Preferences", "(Default)", RegistryValueType.Json, RegistryApplyMode.Immediate),
        new(RegistryScope.Workspace, "Workspace\\Browser\\Settings", "(Default)", RegistryValueType.Json, RegistryApplyMode.RestartApplication, "remoteos.browser"),
    ];

    public static RegistrySchemaDefinition? Find(RegistryScope scope, string path, string name) =>
        Definitions.FirstOrDefault(item => item.Scope == scope
            && string.Equals(item.Path, path, StringComparison.Ordinal)
            && string.Equals(item.Name, name, StringComparison.Ordinal));
}
