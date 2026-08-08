namespace RemoteOS.Core.Applications;

/// <summary>
/// Manifest describing a RemoteOS application package.
/// In a real RemoteOS deployment this would be loaded from a package on disk; currently it is
/// constructed in code by the application itself.
/// </summary>
public sealed record ApplicationManifest(
    AppId Id,
    string DisplayName,
    string Version = "1.0.0",
    string? IconGlyph = null,
    string? Description = null,
    IReadOnlyList<string>? RequestedPermissions = null,
    IReadOnlyList<string>? SupportedFileExtensions = null)
{
    /// <summary>Normalised permission identifiers requested by this application package.</summary>
    public IReadOnlyList<string> Permissions => RequestedPermissions?
        .Where(AppPermissions.IsKnown)
        .Distinct(StringComparer.Ordinal)
        .ToArray()
        ?? Array.Empty<string>();

    /// <summary>
    /// Normalized file extensions that this application accepts from RemoteExplorer.
    /// An application with no declared extensions is never offered as a file opener.
    /// </summary>
    public IReadOnlyList<string> FileExtensions => SupportedFileExtensions?
        .Where(extension => !string.IsNullOrWhiteSpace(extension))
        .Select(extension => extension.Trim())
        .Where(extension => extension.StartsWith(".", StringComparison.Ordinal) && extension.Length > 1)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()
        ?? Array.Empty<string>();

    public ApplicationInfo ToInfo() => new(Id, DisplayName, IconGlyph, Description, Permissions, FileExtensions);
}
