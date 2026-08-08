using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOS.Protocol.Desktop;
using RemoteOS.Protocol.Workspace;

namespace Client.Services;

/// <summary>Holds user-facing shell appearance + locale state (wallpaper / theme / time / language / region).
/// 单例，作为桌面外壳的实时 UI 绑定源（<c>DesktopShellView</c> 绑 <c>Settings.CurrentWallpaper</c> 等）。
/// 数据真源在服务端 <see cref="WorkspacePreferencesDto"/>（Workspace 级，多设备同步）；
/// 本类是其在客户端的活副本——登录时由 <c>PreferencesSync</c> 从服务端加载并 <see cref="Apply"/>，
/// 设置应用编辑后即时 <see cref="Apply"/> 反映到外壳，并 fire-and-forget 保存到服务端。</summary>
public sealed partial class ShellSettings : ObservableObject
{
    public IReadOnlyList<WallpaperOption> Wallpapers { get; }

    [ObservableProperty] private int _wallpaperIndex;
    [ObservableProperty] private ThemeKind _theme = ThemeKind.Light;
    [ObservableProperty] private string _timeFormat = WorkspacePreferencesDto.TimeFormat24H;
    [ObservableProperty] private string _dateFormat = "yyyy/M/d";
    [ObservableProperty] private string _language;
    [ObservableProperty] private string _region = WorkspacePreferencesDto.Default.Region;

    public IBrush CurrentWallpaper => Wallpapers[WallpaperIndex].Brush;

    /// <summary>当前壁纸的持久 key（含 <c>builtin:</c> 前缀，与服务端 DTO 对齐）。</summary>
    public string CurrentWallpaperKey =>
        WorkspacePreferencesDto.BuiltInWallpaperPrefix + Wallpapers[WallpaperIndex].Key;

    /// <summary>任务栏 / 开始菜单底色（随主题切换的最小可见主题效果）。</summary>
    public IBrush TaskbarBackground => IsDarkTheme ? Brushes.DarkTaskbar : Brushes.LightTaskbar;

    /// <summary>任务栏前景（时钟 / 图标）。</summary>
    public IBrush TaskbarForeground => IsDarkTheme ? Brushes.LightTaskbarText : Brushes.DarkTaskbarText;

    public bool IsDarkTheme => Theme == ThemeKind.Dark;

    public ShellSettings(LocalLanguageStore localLanguageStore)
    {
        _language = localLanguageStore.Load();
        Wallpapers =
        [
            new WallpaperOption("bloom", "Bloom", Gradient("#EAF4FF", "#D7EAFF", "#B9D9F7")),
            new WallpaperOption("aurora", "Aurora", Gradient("#E7F8F2", "#D4F0E7", "#B6DFD2")),
            new WallpaperOption("sunset", "Sunset", Gradient("#FFF0E8", "#FFE1D2", "#F6C5B3")),
            new WallpaperOption("mist", "Mist", Gradient("#F7F7F7", "#E9EDF2", "#D8E0EA")),
            new WallpaperOption("cobalt", "Cobalt", Gradient("#E8F1FF", "#D5E6FF", "#BDD4F5")),
        ];
        _wallpaperIndex = 0;
    }

    partial void OnWallpaperIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentWallpaper));
        OnPropertyChanged(nameof(CurrentWallpaperKey));
    }

    partial void OnThemeChanged(ThemeKind value)
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(TaskbarBackground));
        OnPropertyChanged(nameof(TaskbarForeground));
    }

    /// <summary>将服务端偏好应用到本地活状态（登录加载 / 设置编辑后回写）。</summary>
    public void Apply(WorkspacePreferencesDto prefs)
    {
        Theme = prefs.Theme;
        TimeFormat = prefs.TimeFormat;
        DateFormat = prefs.DateFormat;
        Language = prefs.Language;
        Region = prefs.Region;
        WallpaperIndex = IndexForKey(prefs.WallpaperKey);
    }

    /// <summary>导出当前活状态为服务端 DTO（保存时用）。</summary>
    public WorkspacePreferencesDto ToPreferences(IReadOnlyList<DefaultAppMappingDto>? defaultApps = null)
        => new(CurrentWallpaperKey, Theme, TimeFormat, DateFormat, Language, Region,
            defaultApps ?? Array.Empty<DefaultAppMappingDto>());

    /// <summary>按 key 设置壁纸（来自设置应用的选择）。</summary>
    public bool TrySetWallpaperKey(string key)
    {
        var idx = IndexForKey(key);
        if (idx < 0) return false;
        WallpaperIndex = idx;
        return true;
    }

    private int IndexForKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        var bare = key.StartsWith(WorkspacePreferencesDto.BuiltInWallpaperPrefix, StringComparison.OrdinalIgnoreCase)
            ? key[WorkspacePreferencesDto.BuiltInWallpaperPrefix.Length..]
            : key;
        for (var i = 0; i < Wallpapers.Count; i++)
            if (string.Equals(Wallpapers[i].Key, bare, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

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

    private static class Brushes
    {
        public static readonly IBrush LightTaskbar = new SolidColorBrush(Color.Parse("#F7F7F7"));
        public static readonly IBrush DarkTaskbar = new SolidColorBrush(Color.Parse("#1F1F1F"));
        public static readonly IBrush DarkTaskbarText = new SolidColorBrush(Color.Parse("#1F1F1F"));
        public static readonly IBrush LightTaskbarText = new SolidColorBrush(Color.Parse("#F0F0F0"));
    }
}
