using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Client.Services;

/// <summary>Holds user-facing shell appearance state (wallpaper etc.). In-memory for the MVP.</summary>
public sealed partial class ShellSettings : ObservableObject
{
    public IReadOnlyList<WallpaperOption> Wallpapers { get; }

    [ObservableProperty] private int _wallpaperIndex;

    public IBrush CurrentWallpaper => Wallpapers[WallpaperIndex].Brush;

    public ShellSettings()
    {
        Wallpapers =
        [
            new WallpaperOption("Bloom", Gradient("#1B2A4A", "#2E4A7D", "#6E8FCB")),
            new WallpaperOption("Aurora", Gradient("#0B3D2E", "#0E5C46", "#2FAE8E")),
            new WallpaperOption("Sunset", Gradient("#3A1C2A", "#7A2E48", "#D9846A")),
            new WallpaperOption("Graphite", Gradient("#161616", "#232323", "#2E2E2E")),
            new WallpaperOption("Cobalt", Gradient("#0A1A3F", "#143A8C", "#3E7BD6")),
        ];
    }

    partial void OnWallpaperIndexChanged(int value) => OnPropertyChanged(nameof(CurrentWallpaper));

    private static IBrush Gradient(string c0, string c1, string c2)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(Color.Parse(c0), 0));
        brush.GradientStops.Add(new GradientStop(Color.Parse(c1), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.Parse(c2), 1));
        return brush;
    }
}
