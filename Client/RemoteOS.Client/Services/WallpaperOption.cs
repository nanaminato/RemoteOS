using Avalonia.Media;

namespace Client.Services;

/// <summary>A selectable desktop wallpaper preset.</summary>
public sealed record WallpaperOption(string Name, IBrush Brush);
