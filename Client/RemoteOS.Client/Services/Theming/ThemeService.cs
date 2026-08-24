using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using RemoteOS.Protocol.Desktop;
using RemoteOS.Protocol.Workspace;

namespace Client.Services.Theming;

/// <summary>Applies the workspace theme atomically through Avalonia resources. Views never own palette values.</summary>
public sealed class ThemeService : IDisposable
{
    private readonly Application _application;
    private readonly ResourceDictionary _paletteResources = new();
    private ThemeKind _mode = ThemeKind.Light;
    private ThemePreferencesDto _preferences = ThemePreferencesDto.Default;

    public ThemeService(Application application)
    {
        _application = application;
        _application.Resources.MergedDictionaries.Add(_paletteResources);
        _application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Apply(ThemeKind.Light, ThemePreferencesDto.Default);
    }

    public void Apply(ThemeKind mode, ThemePreferencesDto? preferences)
    {
        _mode = mode;
        _preferences = preferences ?? ThemePreferencesDto.Default;
        if (Dispatcher.UIThread.CheckAccess()) ApplyCore();
        else Dispatcher.UIThread.Post(ApplyCore);
    }

    private void ApplyCore()
    {
        _application.RequestedThemeVariant = _mode switch
        {
            ThemeKind.Dark => ThemeVariant.Dark,
            ThemeKind.System => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };

        var dark = _mode == ThemeKind.Dark || (_mode == ThemeKind.System && _application.ActualThemeVariant == ThemeVariant.Dark);
        var colors = ThemePalettes.Resolve(_preferences, dark);
        // Validation happens before mutation so an invalid user payload cannot leave half a palette applied.
        if (!ThemePalettes.IsComplete(colors)) colors = ThemePalettes.Resolve(ThemePreferencesDto.Default, dark);
        _paletteResources.Clear();
        foreach (var (name, value) in colors)
            _paletteResources[name + "Color"] = Color.Parse(value);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (_mode == ThemeKind.System) ApplyCore();
    }

    public void Dispose() => _application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
}

internal static class ThemePalettes
{
    private static readonly string[] Required =
    ["AppBackground", "ShellBackground", "Surface", "SurfaceRaised", "SurfaceSunken", "SurfaceHover", "SurfacePressed",
     "TextPrimary", "TextSecondary", "TextTertiary", "TextDisabled", "TextOnAccent", "TextOnDanger", "BorderSubtle",
     "BorderDefault", "BorderStrong", "FocusBorder", "FocusRing", "Accent", "AccentHover", "AccentPressed", "AccentMuted",
     "SelectionBackground", "SelectionForeground", "Success", "SuccessMuted", "Warning", "WarningMuted", "Danger", "DangerHover",
     "DangerPressed", "Info", "TaskbarBackground", "TaskbarForeground", "StartMenuBackground", "WindowFrameBackground",
     "WindowTitleBarBackground", "WindowTitleForeground", "WindowInactiveTitleForeground", "OverlayScrim", "Shadow",
     "DesktopIconHover", "DesktopIconSelected"];

    public static Dictionary<string, string> Resolve(ThemePreferencesDto preferences, bool dark)
    {
        var result = Base(dark);
        var palette = preferences.PaletteId switch
        {
            "builtin:nord" => Nord(dark),
            "builtin:catppuccin" => Catppuccin(dark),
            _ => null,
        };
        if (preferences.PaletteId.StartsWith("custom:", StringComparison.Ordinal))
        {
            var id = preferences.PaletteId[7..];
            palette = preferences.CustomPalettes?.FirstOrDefault(x => x.Id == id && x.Mode == (dark ? "dark" : "light"))?.Colors;
        }
        if (palette is not null) Overlay(result, palette);
        if (!string.IsNullOrWhiteSpace(preferences.AccentOverride) && IsColor(preferences.AccentOverride))
            ApplyAccent(result, preferences.AccentOverride);
        return result;
    }

    public static bool IsComplete(IReadOnlyDictionary<string, string> colors) => Required.All(key => colors.TryGetValue(key, out var value) && IsColor(value));

    private static Dictionary<string, string> Base(bool dark) => dark ? new(StringComparer.Ordinal)
    {
        ["AppBackground"]="#202020", ["ShellBackground"]="#202020", ["Surface"]="#2B2B2B", ["SurfaceRaised"]="#333333", ["SurfaceSunken"]="#252525", ["SurfaceHover"]="#3A3A3A", ["SurfacePressed"]="#454545",
        ["TextPrimary"]="#F5F5F5", ["TextSecondary"]="#C8C8C8", ["TextTertiary"]="#A8A8A8", ["TextDisabled"]="#777777", ["TextOnAccent"]="#FFFFFF", ["TextOnDanger"]="#FFFFFF",
        ["BorderSubtle"]="#3A3A3A", ["BorderDefault"]="#515151", ["BorderStrong"]="#6FAEE8", ["FocusBorder"]="#4CC2FF", ["FocusRing"]="#4CC2FF",
        ["Accent"]="#4CC2FF", ["AccentHover"]="#70D0FF", ["AccentPressed"]="#249DDB", ["AccentMuted"]="#174665", ["SelectionBackground"]="#174665", ["SelectionForeground"]="#FFFFFF",
        ["Success"]="#6CCB5F", ["SuccessMuted"]="#183C1B", ["Warning"]="#FFD166", ["WarningMuted"]="#4A3B14", ["Danger"]="#FF7262", ["DangerHover"]="#FF8C80", ["DangerPressed"]="#D94D40", ["Info"]="#6AB8FF",
        ["TaskbarBackground"]="#242424", ["TaskbarForeground"]="#F5F5F5", ["StartMenuBackground"]="#2B2B2B", ["WindowFrameBackground"]="#2B2B2B", ["WindowTitleBarBackground"]="#333333", ["WindowTitleForeground"]="#F5F5F5", ["WindowInactiveTitleForeground"]="#B0B0B0",
        ["OverlayScrim"]="#99000000", ["Shadow"]="#66000000", ["DesktopIconHover"]="#334CC2FF", ["DesktopIconSelected"]="#554CC2FF",
    } : new(StringComparer.Ordinal)
    {
        ["AppBackground"]="#F3F3F3", ["ShellBackground"]="#F7F7F7", ["Surface"]="#FFFFFF", ["SurfaceRaised"]="#FFFFFF", ["SurfaceSunken"]="#EEF3FA", ["SurfaceHover"]="#F0F0F0", ["SurfacePressed"]="#E5E5E5",
        ["TextPrimary"]="#1F1F1F", ["TextSecondary"]="#616161", ["TextTertiary"]="#72819A", ["TextDisabled"]="#A0A0A0", ["TextOnAccent"]="#FFFFFF", ["TextOnDanger"]="#FFFFFF",
        ["BorderSubtle"]="#E5EAF2", ["BorderDefault"]="#D6DCE5", ["BorderStrong"]="#8AB9E5", ["FocusBorder"]="#0078D4", ["FocusRing"]="#0078D4",
        ["Accent"]="#0078D4", ["AccentHover"]="#1A86D9", ["AccentPressed"]="#005A9E", ["AccentMuted"]="#E6F0FA", ["SelectionBackground"]="#E6F0FA", ["SelectionForeground"]="#1F1F1F",
        ["Success"]="#107C10", ["SuccessMuted"]="#DFF6DD", ["Warning"]="#C77A00", ["WarningMuted"]="#FFF4CE", ["Danger"]="#C42B1C", ["DangerHover"]="#B3271D", ["DangerPressed"]="#8F2117", ["Info"]="#2369A7",
        ["TaskbarBackground"]="#F7F7F7", ["TaskbarForeground"]="#1F1F1F", ["StartMenuBackground"]="#FFFFFF", ["WindowFrameBackground"]="#FFFFFF", ["WindowTitleBarBackground"]="#F5F5F5", ["WindowTitleForeground"]="#202020", ["WindowInactiveTitleForeground"]="#4F4F4F",
        ["OverlayScrim"]="#66000000", ["Shadow"]="#22000000", ["DesktopIconHover"]="#220078D4", ["DesktopIconSelected"]="#330078D4",
    };

    private static Dictionary<string, string> Nord(bool dark) => new(StringComparer.Ordinal)
    {
        ["AppBackground"] = dark ? "#2E3440" : "#ECEFF4", ["ShellBackground"] = dark ? "#2E3440" : "#E5E9F0", ["Surface"] = dark ? "#3B4252" : "#FFFFFF", ["SurfaceRaised"] = dark ? "#434C5E" : "#F8FAFC", ["SurfaceSunken"] = dark ? "#2E3440" : "#E5E9F0",
        ["TextPrimary"] = dark ? "#ECEFF4" : "#2E3440", ["TextSecondary"] = dark ? "#D8DEE9" : "#4C566A", ["TextTertiary"] = dark ? "#B8C2D2" : "#5E6A7D", ["BorderDefault"] = dark ? "#4C566A" : "#D8DEE9", ["Accent"] = "#5E81AC", ["Info"] = "#88C0D0", ["Success"] = "#A3BE8C", ["Warning"] = "#EBCB8B", ["Danger"] = "#BF616A"
    };
    private static Dictionary<string, string> Catppuccin(bool dark) => new(StringComparer.Ordinal)
    {
        ["AppBackground"] = dark ? "#1E1E2E" : "#EFF1F5", ["ShellBackground"] = dark ? "#1E1E2E" : "#E6E9EF", ["Surface"] = dark ? "#313244" : "#FFFFFF", ["SurfaceRaised"] = dark ? "#45475A" : "#F7F8FB", ["SurfaceSunken"] = dark ? "#181825" : "#E6E9EF",
        ["TextPrimary"] = dark ? "#CDD6F4" : "#4C4F69", ["TextSecondary"] = dark ? "#BAC2DE" : "#6C6F85", ["TextTertiary"] = dark ? "#A6ADC8" : "#7C7F93", ["BorderDefault"] = dark ? "#585B70" : "#CCD0DA", ["Accent"] = dark ? "#89B4FA" : "#1E66F5", ["Info"] = dark ? "#89DCEB" : "#04A5E5", ["Success"] = dark ? "#A6E3A1" : "#40A02B", ["Warning"] = dark ? "#F9E2AF" : "#DF8E1D", ["Danger"] = dark ? "#F38BA8" : "#D20F39"
    };
    private static void Overlay(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var (key, value) in source)
        {
            var semantic = char.ToUpperInvariant(key[0]) + key[1..];
            if (target.ContainsKey(semantic) && IsColor(value)) target[semantic] = value;
        }
        ApplyAccent(target, target["Accent"]);
    }
    private static void ApplyAccent(Dictionary<string, string> target, string accent)
    {
        target["Accent"] = accent; target["AccentHover"] = Adjust(accent, 0.12); target["AccentPressed"] = Adjust(accent, -0.16);
        target["AccentMuted"] = WithAlpha(accent, "26"); target["SelectionBackground"] = WithAlpha(accent, "2E"); target["FocusBorder"] = accent; target["FocusRing"] = accent;
    }
    private static bool IsColor(string? value) => value is { Length: 7 or 9 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
    private static string WithAlpha(string color, string alpha) => "#" + alpha + color[^6..];
    private static string Adjust(string color, double amount)
    {
        var c = Color.Parse(color); byte Shift(byte v) => (byte)Math.Clamp(Math.Round(v + (amount < 0 ? v : 255 - v) * amount), 0, 255);
        return $"#{Shift(c.R):X2}{Shift(c.G):X2}{Shift(c.B):X2}";
    }
}
