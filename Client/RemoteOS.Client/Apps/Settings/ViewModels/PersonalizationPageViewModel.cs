using Client.Services;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Desktop;
using RemoteOS.Protocol.Workspace;

namespace Client.Apps.Settings.ViewModels;

/// <summary>「个性化」页：壁纸预设选择 + 主题（Light/Dark/System）。
/// 透传读写 <see cref="ShellSettings"/>，改动即时反映到桌面外壳（壁纸 / 任务栏底色）并触发保存。</summary>
public sealed partial class PersonalizationPageViewModel : SettingsPageViewModel
{
    public PersonalizationPageViewModel(ShellSettings settings, Action? save) : base(settings, save)
    {
        // Theme 变化（含外部 Apply 加载）时刷新三个 RadioButton 绑定。
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Theme))
            {
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(IsSystemTheme));
            }
            else if (e.PropertyName == nameof(ShellSettings.ThemePreferences))
            {
                OnPropertyChanged(nameof(PaletteId));
                OnPropertyChanged(nameof(AccentOverride));
            }
        };
    }

    public override string Glyph => "🎨";
    public override string DisplayNameKey => "settings.page.personalization";
    public override string DisplayName => "Personalization";

    public IReadOnlyList<Client.Services.WallpaperOption> Wallpapers => Settings.Wallpapers;

    /// <summary>由 SettingsApp 提供本机文件选择器；VM 不直接依赖 Avalonia TopLevel。</summary>
    public Func<Task>? RequestCustomWallpaperAsync { get; set; }

    public int WallpaperIndex
    {
        get => Settings.WallpaperIndex;
        set { Settings.WallpaperIndex = value; Save(); }
    }

    public ThemeKind Theme
    {
        get => Settings.Theme;
        set { Settings.Theme = value; Save(); }
    }

    public bool IsLightTheme { get => Theme == ThemeKind.Light; set { if (value) Theme = ThemeKind.Light; } }
    public bool IsDarkTheme { get => Theme == ThemeKind.Dark; set { if (value) Theme = ThemeKind.Dark; } }
    public bool IsSystemTheme { get => Theme == ThemeKind.System; set { if (value) Theme = ThemeKind.System; } }

    /// <summary>Built-ins deliberately share the same semantic token contract in every RemoteOS app.</summary>
    public IReadOnlyList<ThemePaletteChoice> PaletteChoices { get; } =
    [
        new("builtin:remoteos-blue", "RemoteOS Blue"),
        new("builtin:nord", "Nord"),
        new("builtin:catppuccin", "Catppuccin"),
    ];

    public string PaletteId
    {
        get => Settings.ThemePreferences.PaletteId;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Settings.ThemePreferences.PaletteId) return;
            UpdateThemePreferences(value, Settings.ThemePreferences.AccentOverride);
        }
    }

    public string? AccentOverride
    {
        get => Settings.ThemePreferences.AccentOverride;
        set
        {
            var color = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
            if (color is not null && (color.Length is not (7 or 9) || color[0] != '#' || !color[1..].All(Uri.IsHexDigit))) return;
            if (color == Settings.ThemePreferences.AccentOverride) return;
            UpdateThemePreferences(Settings.ThemePreferences.PaletteId, color);
        }
    }

    private void UpdateThemePreferences(string paletteId, string? accent)
    {
        var current = Settings.ThemePreferences;
        Settings.ThemePreferences = new ThemePreferencesDto
        {
            StyleId = "remoteos", PaletteId = paletteId, AccentOverride = accent,
            CustomPalettes = current.CustomPalettes ?? [],
        };
        OnPropertyChanged(nameof(PaletteId));
        OnPropertyChanged(nameof(AccentOverride));
        Save();
    }

    [RelayCommand]
    private async Task ChooseImageAsync()
    {
        if (RequestCustomWallpaperAsync is not null)
            await RequestCustomWallpaperAsync();
    }
}

public sealed record ThemePaletteChoice(string Id, string Name);
