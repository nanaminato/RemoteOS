using System.Text.Json;

namespace RemoteOS.Examples.ServerMonitor;

/// <summary>Preferences owned by this package. The external SDK does not expose host Settings storage.</summary>
public sealed record MonitorSettings(int RefreshIntervalMilliseconds = 1000, int HistoryLength = 60)
{
    public MonitorSettings Normalize() => this with
    {
        RefreshIntervalMilliseconds = Math.Clamp(RefreshIntervalMilliseconds, 1000, 60000),
        HistoryLength = Math.Clamp(HistoryLength, 20, 240),
    };
}

public sealed class MonitorSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteOS", "external-app-settings", "server-monitor.json");

    public MonitorSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(SettingsPath))?.Normalize()
                ?? new MonitorSettings();
        }
        catch (IOException) { return new MonitorSettings(); }
        catch (JsonException) { return new MonitorSettings(); }
    }

    public void Save(MonitorSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (IOException)
        {
            // Monitoring must remain usable even if the local preferences file cannot be written.
        }
    }
}
