using System.Text.Json.Serialization;
using RemoteOS.Protocol.Desktop;

namespace RemoteOS.Protocol.Workspace;

/// <summary>
/// Workspace 级用户偏好（壁纸 / 主题 / 时间格式 / 日期格式 / 语言 / 区域 / 默认程序）。
/// 与 <see cref="TerminalSettingsDto"/> / <see cref="RemoteOS.Protocol.Browser.BrowserSettingsDto"/> 同模式：
/// 作为 <c>OwnsOne + ToJson</c> 挂在 Workspace 上，单列 JSON 文本持久化（新增字段无需改 schema）。
/// 多设备登录同一 Workspace 时共享同一份偏好。
/// </summary>
public sealed record WorkspacePreferencesDto(
    [property: JsonPropertyName("wallpaperKey")] string WallpaperKey,
    [property: JsonPropertyName("theme")] ThemeKind Theme,
    [property: JsonPropertyName("timeFormat")] string TimeFormat,
    [property: JsonPropertyName("dateFormat")] string DateFormat,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("defaultApps")] IReadOnlyList<DefaultAppMappingDto> DefaultApps,
    [property: JsonPropertyName("notepadDefaultEncoding")] string? NotepadDefaultEncoding = TextEncodingPreferences.Default,
    [property: JsonPropertyName("codeEditorDefaultEncoding")] string? CodeEditorDefaultEncoding = TextEncodingPreferences.Default)
{
    // EF Core materializes the scalar JSON properties after constructing the owned type.
    // The public positional constructor cannot be used because DefaultApps is an owned
    // collection navigation rather than a scalar property.
    private WorkspacePreferencesDto()
        : this(string.Empty, default, string.Empty, string.Empty, string.Empty, string.Empty,
            new List<DefaultAppMappingDto>())
    {
    }

    /// <summary>24 小时制标识。</summary>
    public const string TimeFormat24H = "24h";

    /// <summary>12 小时制标识。</summary>
    public const string TimeFormat12H = "12h";

    /// <summary>内置壁纸 key 前缀（客户端预设目录使用）。</summary>
    public const string BuiltInWallpaperPrefix = "builtin:";

    public static WorkspacePreferencesDto Default { get; } = new(
        WallpaperKey: BuiltInWallpaperPrefix + "bloom",
        Theme: ThemeKind.Light,
        TimeFormat: TimeFormat24H,
        DateFormat: "yyyy/M/d",
        Language: "en-US",
        Region: "en-US",
        DefaultApps: Array.Empty<DefaultAppMappingDto>(),
        NotepadDefaultEncoding: TextEncodingPreferences.Default,
        CodeEditorDefaultEncoding: TextEncodingPreferences.Default);
}
