using Avalonia;
using Avalonia.Media;

namespace RemoteOS.UI.Themes;

/// <summary>Semantic resource lookup for controls constructed in C#.</summary>
public static class ThemeResources
{
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        return app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush : Brushes.Transparent;
    }

    public static Color Color(string key)
    {
        var app = Application.Current;
        return app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var value) == true && value is Color color
            ? color : Colors.Transparent;
    }
}
