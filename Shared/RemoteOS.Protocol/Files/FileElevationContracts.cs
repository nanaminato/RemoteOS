using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Files;

/// <summary>One-shot request to prepare privileged file access for the current RemoteOS session.</summary>
public sealed record FileElevationRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("password")] string? Password = null);

/// <summary>Result of testing or granting elevated access for a single file path.</summary>
public sealed record FileElevationResult(
    [property: JsonPropertyName("requiresElevation")] bool RequiresElevation,
    [property: JsonPropertyName("elevated")] bool Elevated,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt = null);
