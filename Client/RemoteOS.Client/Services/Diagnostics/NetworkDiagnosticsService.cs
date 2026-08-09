using System.Diagnostics;
using System.Net.Http.Headers;
using Client.Services.Auth;
using Client.Services.Developer;

namespace Client.Services.Diagnostics;

/// <summary>
/// Host-owned in-memory recorder for RemoteOS API diagnostics. It intentionally records only
/// completed summaries, never request bodies, response bodies, tokens, cookies, or SignalR frames.
/// </summary>
public sealed class NetworkDiagnosticsService : IDisposable
{
    public const int MaximumEntries = 500;
    public const int MaximumEstimatedBytes = 4 * 1024 * 1024;

    private readonly DeveloperModeService _developerMode;
    private readonly Func<IAuthSession> _sessionAccessor;
    private readonly object _gate = new();
    private readonly Queue<(NetworkDiagnosticEntry Entry, int Bytes)> _entries = new();
    private bool _recording;
    private int _estimatedBytes;
    private int _dropped;
    private long _nextId;
    private IAuthSession? _session;

    /// <remarks>
    /// <paramref name="sessionAccessor"/> must remain lazy: this service is created while the
    /// auth typed HttpClient pipeline is being assembled, and resolving <see cref="IAuthSession"/>
    /// there would create a circular dependency.
    /// </remarks>
    public NetworkDiagnosticsService(DeveloperModeService developerMode, Func<IAuthSession> sessionAccessor)
    {
        _developerMode = developerMode;
        _sessionAccessor = sessionAccessor;
        _developerMode.Changed += OnAvailabilityChanged;
    }

    public event EventHandler<NetworkDiagnosticsState>? StateChanged;
    public event EventHandler<NetworkDiagnosticEntry>? EntryCompleted;

    public NetworkDiagnosticsState State
    {
        get
        {
            lock (_gate)
                return GetStateLocked();
        }
    }

    public Task<NetworkDiagnosticsCommandResult> StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NetworkDiagnosticsState state;
        lock (_gate)
        {
            if (!IsAvailableLocked())
                return Task.FromResult(NetworkDiagnosticsCommandResult.Unavailable);
            _recording = true;
            state = GetStateLocked();
        }
        StateChanged?.Invoke(this, state);
        return Task.FromResult(NetworkDiagnosticsCommandResult.Succeeded);
    }

    public Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NetworkDiagnosticsState state;
        lock (_gate)
        {
            _recording = false;
            state = GetStateLocked();
        }
        StateChanged?.Invoke(this, state);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            ClearLocked();
        return Task.CompletedTask;
    }

    public NetworkDiagnosticsSnapshot GetSnapshot(NetworkDiagnosticsQuery? query = null)
    {
        lock (_gate)
        {
            var entries = _entries.Select(item => item.Entry).Where(entry => Matches(entry, query)).ToArray();
            return new NetworkDiagnosticsSnapshot(GetStateLocked(), entries, _dropped);
        }
    }

    internal bool ShouldCapture(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;
        if (uri.IsLoopback && uri.Port == DeveloperModeService.BridgePort)
            return true;
        var serverUrl = GetSession().ServerUrl;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
            return false;
        return Uri.Compare(uri, server, UriComponents.SchemeAndServer, UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    internal void Record(NetworkDiagnosticEntry entry)
    {
        NetworkDiagnosticEntry stored;
        lock (_gate)
        {
            if (!_recording || !IsAvailableLocked())
                return;
            stored = entry with { Id = ++_nextId };
            var size = EstimateSize(stored);
            while (_entries.Count > 0 && (_entries.Count >= MaximumEntries || _estimatedBytes + size > MaximumEstimatedBytes))
            {
                var removed = _entries.Dequeue();
                _estimatedBytes -= removed.Bytes;
                _dropped++;
            }
            if (size > MaximumEstimatedBytes)
            {
                _dropped++;
                return;
            }
            _entries.Enqueue((stored, size));
            _estimatedBytes += size;
        }
        EntryCompleted?.Invoke(this, stored);
    }

    internal static string SanitizePathAndQuery(Uri? uri)
    {
        if (uri is null)
            return string.Empty;
        if (string.IsNullOrEmpty(uri.Query))
            return uri.AbsolutePath;
        var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var index = pair.IndexOf('=');
                var encodedKey = index < 0 ? pair : pair[..index];
                var key = Uri.UnescapeDataString(encodedKey);
                if (IsSensitiveName(key))
                    return $"{encodedKey}=[redacted]";
                return pair;
            });
        return $"{uri.AbsolutePath}?{string.Join('&', pairs)}";
    }

    internal static IReadOnlyDictionary<string, string> SanitizeHeaders(HttpHeaders? headers)
    {
        if (headers is null)
            return new Dictionary<string, string>();
        return headers.Take(32).ToDictionary(
            pair => pair.Key,
            pair => IsSensitiveName(pair.Key) ? "[redacted]" : Truncate(string.Join(", ", pair.Value), 512),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsMediaContent(string? contentType, Uri? requestUri)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
             || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
             || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
             || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
            return true;
        var path = requestUri?.AbsolutePath ?? string.Empty;
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ErrorKind(Exception exception) => exception switch
    {
        TaskCanceledException => "Timeout",
        OperationCanceledException => "Cancelled",
        HttpRequestException => "Connection",
        _ => "Unexpected",
    };

    public void Dispose()
    {
        _developerMode.Changed -= OnAvailabilityChanged;
        if (_session is not null)
            _session.StateChanged -= OnSessionStateChanged;
    }

    private void OnAvailabilityChanged(object? sender, EventArgs args) => DisableAndClearIfUnavailable();

    private void OnSessionStateChanged(object? sender, AuthSessionStateChangedEventArgs args) => DisableAndClearIfUnavailable();

    private void DisableAndClearIfUnavailable()
    {
        NetworkDiagnosticsState state;
        lock (_gate)
        {
            if (IsAvailableLocked())
            {
                state = GetStateLocked();
            }
            else
            {
                _recording = false;
                ClearLocked();
                state = GetStateLocked();
            }
        }
        StateChanged?.Invoke(this, state);
    }

    private bool IsAvailableLocked() => _developerMode.IsEnabled && GetSession().State == AuthSessionState.Authenticated;

    private NetworkDiagnosticsState GetStateLocked() => IsAvailableLocked()
        ? new NetworkDiagnosticsState(true, _recording)
        : new NetworkDiagnosticsState(false, false, !_developerMode.IsEnabled ? "Developer Mode is disabled."
            : "Sign in to use Network Inspector.");

    private IAuthSession GetSession()
    {
        if (_session is not null)
            return _session;

        var session = _sessionAccessor();
        _session = session;
        session.StateChanged += OnSessionStateChanged;
        return session;
    }

    private void ClearLocked()
    {
        _entries.Clear();
        _estimatedBytes = 0;
        _dropped = 0;
    }

    private static bool Matches(NetworkDiagnosticEntry entry, NetworkDiagnosticsQuery? query)
    {
        if (query is null) return true;
        if (query.Kind is { } kind && entry.Kind != kind) return false;
        if (query.IsMedia is { } media && entry.IsMedia != media) return false;
        if (query.FailuresOnly == true && entry.Outcome == NetworkDiagnosticOutcome.Succeeded) return false;
        return string.IsNullOrWhiteSpace(query.Text)
            || entry.Source.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
            || entry.PathAndQuery.Contains(query.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static int EstimateSize(NetworkDiagnosticEntry entry) => 256
        + entry.Source.Length * 2 + entry.Name.Length * 2 + entry.PathAndQuery.Length * 2
        + (entry.ContentType?.Length ?? 0) * 2 + (entry.ErrorKind?.Length ?? 0) * 2
        + (entry.RequestHeaders?.Sum(pair => pair.Key.Length + pair.Value.Length) ?? 0) * 2
        + (entry.ResponseHeaders?.Sum(pair => pair.Key.Length + pair.Value.Length) ?? 0) * 2;

    private static bool IsSensitiveName(string name) => name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
        || name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("key", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";
}
