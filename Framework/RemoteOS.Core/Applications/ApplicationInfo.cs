namespace RemoteOS.Core.Applications;

/// <summary>Public metadata for a registered application (shown in desktop / start menu / taskbar).</summary>
public sealed record ApplicationInfo(
    AppId Id,
    string DisplayName,
    string? IconGlyph = null,
    string? Description = null,
    IReadOnlyList<string>? RequestedPermissions = null,
    IReadOnlyList<string>? SupportedFileExtensions = null,
    string Version = "1.0.0")
{
    public IReadOnlyList<string> Permissions => RequestedPermissions ?? Array.Empty<string>();

    /// <summary>File extensions this application explicitly accepts from RemoteExplorer.</summary>
    public IReadOnlyList<string> FileExtensions => SupportedFileExtensions ?? Array.Empty<string>();

    public static readonly ApplicationInfo None = new(default, string.Empty);
}
