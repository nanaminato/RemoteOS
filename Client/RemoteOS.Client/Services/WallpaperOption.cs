using Avalonia.Media;

namespace Client.Services;

/// <summary>A selectable desktop wallpaper preset. <see cref="Key"/> is the persisted identifier
/// (without the <c>builtin:</c> prefix stored in <see cref="RemoteOS.Protocol.Workspace.WorkspacePreferencesDto"/>).</summary>
public sealed record WallpaperOption(string Key, string Name, IBrush Brush);
