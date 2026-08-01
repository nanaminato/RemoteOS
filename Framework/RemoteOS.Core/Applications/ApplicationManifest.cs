namespace RemoteOS.Core.Applications;

/// <summary>
/// Manifest describing a RemoteOS application package.
/// In a real RemoteOS deployment this would be loaded from a package on disk; for the MVP it is
/// constructed in code by the application itself.
/// </summary>
public sealed record ApplicationManifest(
    AppId Id,
    string DisplayName,
    string Version = "1.0.0",
    string? IconGlyph = null,
    string? Description = null)
{
    public ApplicationInfo ToInfo() => new(Id, DisplayName, IconGlyph, Description);
}
