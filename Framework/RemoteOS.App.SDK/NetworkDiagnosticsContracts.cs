namespace RemoteOS.AppSDK;

/// <summary>Stable identity reserved for the optional developer Network Inspector package.</summary>
public static class NetworkDiagnosticsApplication
{
    public const string InspectorAppId = "com.remoteos.dev.network-inspector";
}

/// <summary>Read-only, host-mediated diagnostics for the current RemoteOS client session.</summary>
/// <remarks>The host always redacts credentials and never exposes network payloads for media or SignalR frames.</remarks>
public interface INetworkDiagnostics
{
    NetworkDiagnosticsState State { get; }
    event EventHandler<NetworkDiagnosticsState>? StateChanged;
    event EventHandler<NetworkDiagnosticEntry>? EntryCompleted;

    NetworkDiagnosticsSnapshot GetSnapshot(NetworkDiagnosticsQuery? query = null);
    Task<NetworkDiagnosticsCommandResult> StartRecordingAsync(CancellationToken cancellationToken = default);
    Task StopRecordingAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record NetworkDiagnosticsState(bool IsAvailable, bool IsRecording, string? UnavailableReason = null);

public enum NetworkDiagnosticsCommandResult
{
    Succeeded,
    PermissionDenied,
    Unavailable,
}

public enum NetworkDiagnosticKind { Http, SignalR }
public enum NetworkDiagnosticOutcome { Succeeded, Failed, Cancelled, TransportError }

/// <summary>One completed, already-sanitized network operation.</summary>
public sealed record NetworkDiagnosticEntry(
    long Id,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    NetworkDiagnosticKind Kind,
    string Source,
    string Name,
    string? Method,
    string PathAndQuery,
    NetworkDiagnosticOutcome Outcome,
    int? StatusCode,
    string? ContentType,
    long? DeclaredContentLength,
    bool IsMedia,
    string? ErrorKind,
    IReadOnlyDictionary<string, string>? RequestHeaders = null,
    IReadOnlyDictionary<string, string>? ResponseHeaders = null);

public sealed record NetworkDiagnosticsQuery(
    string? Text = null,
    NetworkDiagnosticKind? Kind = null,
    bool? IsMedia = null,
    bool? FailuresOnly = null);

public sealed record NetworkDiagnosticsSnapshot(
    NetworkDiagnosticsState State,
    IReadOnlyList<NetworkDiagnosticEntry> Entries,
    int DroppedEntryCount);
