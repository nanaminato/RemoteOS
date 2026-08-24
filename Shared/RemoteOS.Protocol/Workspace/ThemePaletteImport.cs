using System.Text.RegularExpressions;

namespace RemoteOS.Protocol.Workspace;

/// <summary>Normalises an imported custom palette before it becomes part of workspace preferences.</summary>
public static class ThemePaletteImport
{
    public static bool TryNormalize(
        ThemePaletteDto? source,
        IEnumerable<string> existingIds,
        string? accentOverride,
        out ThemePaletteDto? palette,
        out ThemePaletteImportError error)
    {
        palette = null;
        error = ThemePaletteImportError.InvalidFormat;
        if (source is null || source.FormatVersion is not (1 or 2)
            || string.IsNullOrWhiteSpace(source.Name) || source.Name.Trim().Length > 80)
            return false;

        var light = source.FormatVersion == 1 ? source.Colors : source.LightColors;
        var dark = source.FormatVersion == 1 ? source.Colors : source.DarkColors;
        if (!AreValidColors(light) || !AreValidColors(dark)) return false;

        palette = new ThemePaletteDto
        {
            FormatVersion = 2,
            Id = MakeUniqueId(source.Id, existingIds),
            Name = source.Name.Trim(),
            LightColors = NormalizeColors(light!),
            DarkColors = NormalizeColors(dark!),
        };
        var preferences = new ThemePreferencesDto
        {
            PaletteId = "custom:" + palette.Id,
            AccentOverride = accentOverride,
            CustomPalettes = [palette],
        };
        if (ThemePaletteValidator.TryValidate(ThemePaletteDefaults.Resolve(preferences, dark: false), out _)
            && ThemePaletteValidator.TryValidate(ThemePaletteDefaults.Resolve(preferences, dark: true), out _))
        {
            error = ThemePaletteImportError.None;
            return true;
        }

        palette = null;
        error = ThemePaletteImportError.Inaccessible;
        return false;
    }

    private static bool AreValidColors(Dictionary<string, string>? colors) => colors is { Count: > 0 and <= 56 }
        && colors.All(pair => ThemePaletteContract.ColorTokens.Contains(pair.Key) && ThemePaletteDefaults.IsColor(pair.Value));

    private static Dictionary<string, string> NormalizeColors(Dictionary<string, string> colors) => colors
        .ToDictionary(pair => pair.Key, pair => ThemePaletteDefaults.Normalize(pair.Value), StringComparer.OrdinalIgnoreCase);

    private static string MakeUniqueId(string? requestedId, IEnumerable<string> existingIds)
    {
        var existing = new HashSet<string>(existingIds, StringComparer.Ordinal);
        var seed = Regex.Replace((requestedId ?? "theme").Trim().ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        if (string.IsNullOrEmpty(seed)) seed = "theme";
        seed = seed[..Math.Min(seed.Length, 56)];
        for (var suffix = 1; ; suffix++)
        {
            var id = suffix == 1 ? seed : $"{seed}-{suffix}";
            if (id.Length <= 64 && existing.Add(id)) return id;
        }
    }
}

public enum ThemePaletteImportError
{
    None,
    InvalidFormat,
    Inaccessible,
}
