using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Privileged;

/// <summary>
/// Local-only request sent by RemoteOS Server to the installed privileged helper.
/// This is intentionally not an HTTP contract.
/// </summary>
public sealed record PrivilegedOperationRequest(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("contentBase64")] string? ContentBase64 = null,
    [property: JsonPropertyName("executable")] string? Executable = null,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string>? Arguments = null,
    [property: JsonPropertyName("standardInputBase64")] string? StandardInputBase64 = null);

/// <summary>Result returned by the local privileged helper over its standard output.</summary>
public sealed record PrivilegedOperationResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("exitCode")] int ExitCode = 0,
    [property: JsonPropertyName("outputBase64")] string? OutputBase64 = null,
    [property: JsonPropertyName("error")] string? Error = null);
