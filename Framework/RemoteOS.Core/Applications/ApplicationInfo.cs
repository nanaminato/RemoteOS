namespace RemoteOS.Core.Applications;

/// <summary>Public metadata for a registered application (shown in desktop / start menu / taskbar).</summary>
public sealed record ApplicationInfo(
    AppId Id,
    string DisplayName,
    string? IconGlyph = null,
    string? Description = null)
{
    public static readonly ApplicationInfo None = new(default, string.Empty);
}
