using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Client.Services.Auth;

namespace Client.Apps.PortForwarding;

/// <summary>Starts and supervises loopback-only <c>ssh -L</c> processes on the Client host.</summary>
public sealed class PortForwardingService : IPortForwardingService
{
    private const int StartupWaitMilliseconds = 650;
    private readonly IAuthSession _session;
    private readonly PortForwardingSettingsStore _settingsStore;
    private readonly ConcurrentDictionary<Guid, RunningForward> _forwards = new();
    private PortForwardingSettings _settings;

    public PortForwardingService(IAuthSession session, PortForwardingSettingsStore settingsStore)
    {
        _session = session;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
    }

    public event EventHandler? ForwardsChanged;

    public IReadOnlyList<PortForwardInfo> List() => _forwards.Values
        .Select(forward => forward.Info)
        .OrderBy(forward => forward.LocalPort)
        .ToArray();

    public PortForwardingSettings GetSettings() => _settings;

    public void SaveSettings(PortForwardingSettings settings)
    {
        _settings = settings.Normalize();
        _settingsStore.Save(_settings);
    }

    public async Task<PortForwardInfo> StartAsync(PortForwardRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var existing = _forwards.Values.FirstOrDefault(forward =>
            forward.Info.RemotePort == request.RemotePort
            && forward.Info.RemoteHost.Equals(request.RemoteHost, StringComparison.OrdinalIgnoreCase)
            && forward.Info.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing.Info with { PathAndQuery = request.PathAndQuery };

        var server = ResolveSshServer();
        var localPort = FindAvailablePort(request.PreferredLocalPort ?? request.RemotePort);
        var process = CreateSshProcess(server, request, localPort);
        if (!process.Start())
            throw new InvalidOperationException("Unable to start the local SSH client.");
        try { await Task.Delay(StartupWaitMilliseconds, cancellationToken); }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        if (process.HasExited)
        {
            process.Dispose();
            throw new InvalidOperationException("SSH exited before the port forward became available. Check the SSH host, user, and key agent configuration.");
        }

        var id = Guid.NewGuid();
        var info = new PortForwardInfo(id, request.RemoteHost, request.RemotePort, localPort,
            request.Scheme, request.PathAndQuery, DateTimeOffset.UtcNow, "Running");
        var running = new RunningForward(info, process);
        if (!_forwards.TryAdd(id, running))
        {
            Stop(process);
            throw new InvalidOperationException("Could not register the started port forward.");
        }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(id, process);
        RaiseChanged();
        return info;
    }

    public async Task<PortForwardInfo> UpdateAsync(Guid id, PortForwardRequest request, CancellationToken cancellationToken = default)
    {
        // A modification replaces one owned SSH process. The requested local port is checked
        // again, so an occupied port receives the same predictable fallback as a new request.
        await RemoveAsync(id, cancellationToken);
        return await StartAsync(request, cancellationToken);
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_forwards.TryRemove(id, out var running))
        {
            Stop(running.Process);
            RaiseChanged();
        }
        return Task.CompletedTask;
    }

    private (string Host, string? User, int Port) ResolveSshServer()
    {
        var host = _settings.SshHost;
        var user = _settings.SshUser;
        if (string.IsNullOrWhiteSpace(host))
        {
            if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } serverUrl }
                || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
                throw new InvalidOperationException("Connect to RemoteOS first, or set an SSH host in Port Forwarding settings.");
            host = serverUri.Host;
        }
        user ??= _session.CurrentUser?.Username;
        if (host.StartsWith('-', StringComparison.Ordinal) || host.Any(char.IsWhiteSpace)
            || user?.StartsWith('-', StringComparison.Ordinal) == true
            || user?.Any(char.IsWhiteSpace) == true)
            throw new InvalidOperationException("SSH host and user cannot contain whitespace or start with '-'.");
        return (host, user, _settings.SshPort);
    }

    private static Process CreateSshProcess((string Host, string? User, int Port) server, PortForwardRequest request, int localPort)
    {
        var start = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-N");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("BatchMode=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ExitOnForwardFailure=yes");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-L");
        start.ArgumentList.Add($"127.0.0.1:{localPort}:{request.RemoteHost}:{request.RemotePort}");
        start.ArgumentList.Add(string.IsNullOrWhiteSpace(server.User) ? server.Host : $"{server.User}@{server.Host}");
        return new Process { StartInfo = start };
    }

    private static int FindAvailablePort(int preferred)
    {
        var first = Math.Clamp(preferred, 1, 65535);
        for (var offset = 0; offset <= 65535 - first; offset++)
        {
            var port = first + offset;
            if (CanBind(port)) return port;
        }
        for (var port = 1; port < first; port++)
            if (CanBind(port)) return port;
        throw new InvalidOperationException("No loopback TCP port is available on this host.");
    }

    private static bool CanBind(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException) { return false; }
        finally { listener?.Stop(); }
    }

    private static void Validate(PortForwardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isLoopbackTarget = request.RemoteHost?.Equals("localhost", StringComparison.OrdinalIgnoreCase) == true
                               || request.RemoteHost?.Equals("127.0.0.1", StringComparison.Ordinal) == true;
        if (!isLoopbackTarget)
            throw new ArgumentException("Only localhost and 127.0.0.1 services on the RemoteOS server can be forwarded.", nameof(request));
        if (request.RemotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), "The remote port must be between 1 and 65535.");
        if (request.PreferredLocalPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), "The preferred local port must be between 1 and 65535.");
        var isWebScheme = request.Scheme?.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) == true
                          || request.Scheme?.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) == true;
        if (!isWebScheme)
            throw new ArgumentException("Only HTTP and HTTPS links can be returned by this application.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PathAndQuery)
            || !request.PathAndQuery.StartsWith('/', StringComparison.Ordinal))
            throw new ArgumentException("The forwarded link path must start with '/'.", nameof(request));
    }

    private void OnProcessExited(Guid id, Process process)
    {
        if (_forwards.TryRemove(id, out _))
            RaiseChanged();
        process.Dispose();
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private void RaiseChanged() => ForwardsChanged?.Invoke(this, EventArgs.Empty);

    private sealed record RunningForward(PortForwardInfo Info, Process Process);
}
