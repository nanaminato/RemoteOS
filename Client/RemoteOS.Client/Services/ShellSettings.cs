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
            new WallpaperOption("Bloom", Gradient("#EAF4FF", "#D7EAFF", "#B9D9F7")),
            new WallpaperOption("Aurora", Gradient("#E7F8F2", "#D4F0E7", "#B6DFD2")),
            new WallpaperOption("Sunset", Gradient("#FFF0E8", "#FFE1D2", "#F6C5B3")),
            new WallpaperOption("Mist", Gradient("#F7F7F7", "#E9EDF2", "#D8E0EA")),
            new WallpaperOption("Cobalt", Gradient("#E8F1FF", "#D5E6FF", "#BDD4F5")),
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
