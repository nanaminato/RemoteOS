using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Browser;

/// <summary>Persistent per-workspace browser preferences.</summary>
public sealed record BrowserSettingsDto(
    [property: JsonPropertyName("localPortForwardingEnabled")] bool LocalPortForwardingEnabled)
{
    public static BrowserSettingsDto Default { get; } = new(LocalPortForwardingEnabled: false);
}
