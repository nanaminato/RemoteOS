using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Workspace;

/// <summary>Workspace-synchronised theme choice. Palettes are data only; no AXAML is accepted.</summary>
public sealed record ThemePreferencesDto
{
    public const string DefaultPaletteId = "builtin:remoteos-blue";

    [JsonPropertyName("styleId")] public string StyleId { get; set; } = "remoteos";
    [JsonPropertyName("paletteId")] public string PaletteId { get; set; } = DefaultPaletteId;
    [JsonPropertyName("accentOverride")] public string? AccentOverride { get; set; }
    [JsonPropertyName("customPalettes")] public List<ThemePaletteDto> CustomPalettes { get; set; } = [];

    public static ThemePreferencesDto Default => new();
}

/// <summary>Safe, serialisable palette payload. It intentionally contains only named sRGB values.</summary>
public sealed record ThemePaletteDto
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; set; } = string.Empty;
    [JsonPropertyName("colors")] public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
