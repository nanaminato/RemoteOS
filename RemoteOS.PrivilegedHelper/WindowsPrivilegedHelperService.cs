using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Runtime.Versioning;
using RemoteOS.Protocol.Privileged;

namespace RemoteOS.PrivilegedHelper;

/// <summary>
/// LocalSystem-only Windows service. It accepts a bounded, authenticated local pipe request and
/// invokes this signed Helper apphost as a one-shot worker; it never accepts a caller command.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrivilegedHelperService : ServiceBase
{
    private readonly WindowsHelperServiceConfiguration _configuration;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentOperationIds = new();
    private Task? _listener;

    private WindowsPrivilegedHelperService(WindowsHelperServiceConfiguration configuration)
    {
        ServiceName = "RemoteOSPrivilegedHelper";
        CanStop = true;
        AutoLog = true;
        _configuration = configuration;
    }

    public static void Run(string[] args)
    {
        var pathIndex = Array.FindIndex(args, argument => string.Equals(argument, "--config", StringComparison.Ordinal));
        if (pathIndex < 0 || pathIndex + 1 >= args.Length) throw new InvalidOperationException("--config is required for the Windows Helper service.");
        var path = Path.GetFullPath(args[pathIndex + 1]);
        var configuration = JsonSerializer.Deserialize<WindowsHelperServiceConfiguration>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Windows Helper configuration is invalid.");
        configuration.Validate();
        configuration.VerifyCurrentExecutable();
        Environment.SetEnvironmentVariable("REMOTEOS_PRIVILEGED_FILE_ROOTS", string.Join(Path.PathSeparator, configuration.FileAllowedRoots));
        Environment.SetEnvironmentVariable("REMOTEOS_PRIVILEGED_SERVICE_IDS", string.Join(Path.PathSeparator, configuration.AllowedServiceIds));
        ServiceBase.Run(new WindowsPrivilegedHelperService(configuration));
    }

    protected override void OnStart(string[] args) => _listener = Task.Run(ListenAsync);

    protected override void OnStop()
    {
        _stopping.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stopping.Dispose();
        base.Dispose(disposing);
    }

    private async Task ListenAsync()
    {
        var secret = Convert.FromBase64String(_configuration.SharedSecret);
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(_stopping.Token);
                await HandleAsync(pipe, secret, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception exception)
            {
                EventLog.WriteEntry(ServiceName, $"Privileged Helper pipe request failed: {exception.GetType().Name}", EventLogEntryType.Warning);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(_configuration.ServerServiceSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_configuration.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, PrivilegedOperationProtocol.MaximumRequestBytes,
            PrivilegedOperationProtocol.MaximumRequestBytes, security);
    }

    private async Task HandleAsync(Stream pipe, byte[] secret, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync(pipe, cancellationToken);
        var envelope = JsonSerializer.Deserialize<PipeEnvelope>(frame);
        if (envelope is null || !TryDecodeAndVerify(secret, envelope, out var requestJson)) return;
        var request = JsonSerializer.Deserialize<PrivilegedOperationRequest>(requestJson);
        if (request is null || request.Version != PrivilegedOperationProtocol.Version)
        {
            await WriteResultAsync(pipe, secret, new(false, 64, Error: "unsupported protocol version", ProblemCode: PrivilegedProblemCode.InvalidProtocol), cancellationToken);
            return;
        }
        if (request.OperationId is not { } operationId || operationId == Guid.Empty)
        {
            await WriteResultAsync(pipe, secret, new(false, 64, Error: "operation id is required", ProblemCode: PrivilegedProblemCode.InvalidRequest), cancellationToken);
            return;
        }
        PruneRecentOperationIds();
        if (!_recentOperationIds.TryAdd(operationId, DateTimeOffset.UtcNow.AddMinutes(10)))
        {
            await WriteResultAsync(pipe, secret, new(false, 17, Error: "operation id was already processed", ProblemCode: PrivilegedProblemCode.Conflict), cancellationToken);
            return;
        }
        var result = await RunWorkerAsync(requestJson, cancellationToken);
        await WriteResultAsync(pipe, secret, result, cancellationToken);
    }

    private void PruneRecentOperationIds()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _recentOperationIds.Where(pair => pair.Value <= now))
            _recentOperationIds.TryRemove(pair.Key, out _);
    }

    private static async Task<PrivilegedOperationResult> RunWorkerAsync(byte[] requestJson, CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            return new(false, 69, Error: "Helper worker path unavailable", ProblemCode: PrivilegedProblemCode.HelperUnavailable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        using var worker = Process.Start(start);
        if (worker is null) return new(false, 69, Error: "Helper worker could not start", ProblemCode: PrivilegedProblemCode.HelperUnavailable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        await worker.StandardInput.BaseStream.WriteAsync(requestJson, timeout.Token);
        await worker.StandardInput.DisposeAsync();
        var output = worker.StandardOutput.ReadToEndAsync(timeout.Token);
        try { await worker.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { worker.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(false, 124, Error: "Helper worker timed out", ProblemCode: PrivilegedProblemCode.TimedOut);
        }
        try
        {
            return JsonSerializer.Deserialize<PrivilegedOperationResult>(await output)
                   ?? new(false, 69, Error: "Helper worker returned no result", ProblemCode: PrivilegedProblemCode.HelperUnavailable);
        }
        catch (JsonException)
        {
            return new(false, 69, Error: "Helper worker returned an invalid result", ProblemCode: PrivilegedProblemCode.HelperUnavailable);
        }
    }

    private static async Task WriteResultAsync(Stream pipe, byte[] secret, PrivilegedOperationResult result, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(result);
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new PipeEnvelope(Convert.ToBase64String(payload), Sign(secret, payload))), cancellationToken);
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BitConverter.ToInt32(header);
        if (length is < 1 or > PrivilegedOperationProtocol.MaximumRequestBytes) throw new InvalidDataException("invalid pipe request size");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            buffer = buffer[read..];
        }
    }

    private static string Sign(byte[] secret, byte[] payload) => Convert.ToBase64String(HMACSHA256.HashData(secret, payload));
    private static bool TryDecodeAndVerify(byte[] secret, PipeEnvelope envelope, out byte[] payload)
    {
        payload = [];
        try
        {
            payload = Convert.FromBase64String(envelope.PayloadBase64);
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(envelope.SignatureBase64), HMACSHA256.HashData(secret, payload));
        }
        catch (FormatException) { return false; }
    }

    private sealed record PipeEnvelope(string PayloadBase64, string SignatureBase64);
}

[SupportedOSPlatform("windows")]
public sealed record WindowsHelperServiceConfiguration(string PipeName, string SharedSecret, string ServerServiceSid,
    IReadOnlyList<string> FileAllowedRoots, IReadOnlyList<string> AllowedServiceIds, string HelperExecutableSha256)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 128 || string.IsNullOrWhiteSpace(ServerServiceSid)
            || FileAllowedRoots.Count == 0 || FileAllowedRoots.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            || AllowedServiceIds.Count == 0 || AllowedServiceIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256)
            || string.IsNullOrWhiteSpace(HelperExecutableSha256) || !System.Text.RegularExpressions.Regex.IsMatch(HelperExecutableSha256, "^[0-9a-fA-F]{64}$"))
            throw new InvalidOperationException("Windows Helper configuration is incomplete.");
        if (Convert.FromBase64String(SharedSecret).Length < 32) throw new InvalidOperationException("Windows Helper secret is too short.");
        _ = new SecurityIdentifier(ServerServiceSid);
    }

    public void VerifyCurrentExecutable()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("Windows Helper executable is unavailable.");
        var expected = Convert.FromHexString(HelperExecutableSha256);
        using var stream = File.OpenRead(executable);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidOperationException("Windows Helper executable integrity verification failed.");
    }
}
