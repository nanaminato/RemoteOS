using Avalonia;
using Avalonia.Media;

namespace Client.Services.Theming;

/// <summary>Bridge for controls constructed in C#. Returned brushes track the dynamic colour token.</summary>
public static class ThemeBrushes
{
    public static IBrush Get(string key)
    {
        var app = Application.Current;
        return app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush : Brushes.Transparent;
    }
}
