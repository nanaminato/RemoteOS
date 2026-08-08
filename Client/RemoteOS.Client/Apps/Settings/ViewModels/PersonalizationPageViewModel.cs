using Client.Services;
using RemoteOS.Protocol.Desktop;

namespace Client.Apps.Settings.ViewModels;

/// <summary>「个性化」页：壁纸预设选择 + 主题（Light/Dark/System）。
/// 透传读写 <see cref="ShellSettings"/>，改动即时反映到桌面外壳（壁纸 / 任务栏底色）并触发保存。</summary>
public sealed class PersonalizationPageViewModel : SettingsPageViewModel
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
        };
    }

    public override string Glyph => "🎨";
    public override string DisplayNameKey => "settings.page.personalization";
    public override string DisplayName => "Personalization";

    public IReadOnlyList<Client.Services.WallpaperOption> Wallpapers => Settings.Wallpapers;

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
}
