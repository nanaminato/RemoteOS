using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.ProcessGuardian;

namespace RemoteOS.Guardian.Agent;

/// <summary>Owns child processes, their restart generations, and the minimal replayable snapshot.</summary>
internal sealed partial class WorkloadSupervisor
{
    private readonly GuardianAgentOptions _options;
    private readonly Dictionary<string, ManagedWorkload> _workloads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string DefinitionsPath => Path.Combine(_options.DataDirectory, "workloads.json");
    private string AuditPath => Path.Combine(_options.DataDirectory, "audit.jsonl");

    public WorkloadSupervisor(GuardianAgentOptions options) => _options = options;

    public async Task RestoreEnabledWorkloadsAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        if (!File.Exists(DefinitionsPath)) return;
        var definitions = JsonSerializer.Deserialize<IReadOnlyList<ProcessDefinitionDto>>(await File.ReadAllTextAsync(DefinitionsPath, cancellationToken), RemoteOsJsonOptions.Default) ?? [];
        foreach (var definition in definitions)
        {
            if (!Validate(definition, out _)) continue;
            var workload = new ManagedWorkload(definition) { DesiredState = definition.EnabledOnBoot ? "Running" : "Stopped" };
            _workloads[definition.Id] = workload;
            if (definition.EnabledOnBoot) await StartAsync(workload, cancellationToken);
        }
    }

    public async Task<GuardianAgentResponse> HandleAsync(GuardianAgentRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            GuardianAgentResponse response = request.Command switch
            {
                "status" => new GuardianAgentResponse(true, string.Empty, new GuardianStatusDto(true, true, string.Empty, "0.1.0")),
                "list" => new GuardianAgentResponse(true, string.Empty, Workloads: _workloads.Values.Select(ToDto).ToArray()),
                "upsert" => await UpsertAsync(request.Definition, cancellationToken),
                "start" => await ApplyAsync(request.WorkloadId, StartAsync, cancellationToken),
                "stop" => await ApplyAsync(request.WorkloadId, StopAsync, cancellationToken),
                "restart" => await RestartAsync(request.WorkloadId, cancellationToken),
                "logs" => Logs(request.WorkloadId),
                "audit" => await ReadAuditAsync(cancellationToken),
                _ => new GuardianAgentResponse(false, "guardian.ipc_invalid_command"),
            };
            if (request.Command is "upsert" or "start" or "stop" or "restart")
                await WriteAuditAsync(request.Command, request.WorkloadId ?? request.Definition?.Id, response, cancellationToken);
            return response;
        }
        finally { _gate.Release(); }
    }

    private async Task<GuardianAgentResponse> UpsertAsync(ProcessDefinitionDto? definition, CancellationToken cancellationToken)
    {
        if (definition is null) return new GuardianAgentResponse(false, "guardian.validation_failed");
        if (!Validate(definition, out var problem)) return new GuardianAgentResponse(false, problem ?? "guardian.validation_failed");
        if (_workloads.TryGetValue(definition.Id, out var existing) && existing.Process is { HasExited: false })
            return new GuardianAgentResponse(false, "guardian.workload_running");
        _workloads[definition.Id] = new ManagedWorkload(definition) { DesiredState = definition.EnabledOnBoot ? "Running" : "Stopped" };
        await PersistAsync(cancellationToken);
        return new GuardianAgentResponse(true, string.Empty);
    }

    private async Task<GuardianAgentResponse> ApplyAsync(string? workloadId, Func<ManagedWorkload, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workloadId) || !_workloads.TryGetValue(workloadId, out var workload)) return new GuardianAgentResponse(false, "guardian.workload_not_found");
        await action(workload, cancellationToken); return new GuardianAgentResponse(true, string.Empty);
    }

    private async Task<GuardianAgentResponse> RestartAsync(string? workloadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workloadId) || !_workloads.TryGetValue(workloadId, out var workload)) return new GuardianAgentResponse(false, "guardian.workload_not_found");
        await StopAsync(workload, cancellationToken); await StartAsync(workload, cancellationToken); return new GuardianAgentResponse(true, string.Empty);
    }

    private GuardianAgentResponse Logs(string? workloadId)
    {
        if (string.IsNullOrWhiteSpace(workloadId) || !_workloads.TryGetValue(workloadId, out var workload)) return new GuardianAgentResponse(false, "guardian.workload_not_found");
        return new GuardianAgentResponse(true, string.Empty, Logs: workload.Logs.ToArray());
    }

    private async Task StartAsync(ManagedWorkload workload, CancellationToken cancellationToken)
    {
        if (workload.Process is { HasExited: false }) return;
        workload.DesiredState = "Running"; workload.ActualState = "Starting"; workload.Generation++;
        var start = new ProcessStartInfo(workload.Definition.ExecutablePath) { UseShellExecute = false, WorkingDirectory = workload.Definition.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in workload.Definition.Arguments) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (Exception) { workload.ActualState = "Failed"; return; }
        if (process is null) { workload.ActualState = "Failed"; return; }
        workload.Process = process; workload.ActualState = "Running";
        _ = CaptureOutputAsync(process.StandardOutput, workload, "stdout");
        _ = CaptureOutputAsync(process.StandardError, workload, "stderr");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = HandleExitAsync(workload, process.ExitCode);
        await Task.CompletedTask;
    }

    private static async Task StopAsync(ManagedWorkload workload, CancellationToken cancellationToken)
    {
        workload.DesiredState = "Stopped"; workload.ActualState = "Stopping";
        var process = workload.Process;
        if (process is null || process.HasExited) { workload.ActualState = "Stopped"; return; }
        if (OperatingSystem.IsWindows() && process.CloseMainWindow()) await Task.Delay(TimeSpan.FromSeconds(workload.Definition.StopTimeoutSeconds), cancellationToken);
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken); workload.ActualState = "Stopped";
    }

    private async Task HandleExitAsync(ManagedWorkload workload, int exitCode)
    {
        workload.ExitCode = exitCode;
        if (workload.DesiredState != "Running" || exitCode == 0)
        {
            workload.ActualState = "Stopped";
            return;
        }
        if (workload.RestartCount >= workload.Definition.MaxRestartAttempts)
        {
            workload.ActualState = "CrashLoop";
            return;
        }
        workload.RestartCount++;
        workload.ActualState = "Backoff";
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, workload.RestartCount)), CancellationToken.None);
            if (workload.DesiredState == "Running") await StartAsync(workload, CancellationToken.None);
        }
        catch
        {
            workload.ActualState = "Failed";
        }
    }

    /// <summary>Runs independently of HTTP availability and only changes actual state from observed checks.</summary>
    public async Task RunHealthChecksAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    foreach (var workload in _workloads.Values.Where(item => item.Process is { HasExited: false } && item.Definition.HealthCheck is not null && DateTimeOffset.UtcNow >= item.NextHealthCheckAt))
                    {
                        var check = workload.Definition.HealthCheck!;
                        workload.NextHealthCheckAt = DateTimeOffset.UtcNow.AddSeconds(check.IntervalSeconds);
                        var healthy = await CheckHealthAsync(check, cancellationToken);
                        workload.HealthStatus = healthy ? "Healthy" : "Unhealthy";
                        workload.HealthFailureCount = healthy ? 0 : workload.HealthFailureCount + 1;
                        if (!healthy && workload.HealthFailureCount >= check.FailureThreshold)
                        {
                            workload.ActualState = "Degraded";
                            await WriteAuditAsync("health_check", workload.Definition.Id, new GuardianAgentResponse(false, "guardian.health_check_failed"), cancellationToken);
                        }
                    }
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { /* A failed probe must never terminate the Agent. */ }
        }
    }

    private static async Task<bool> CheckHealthAsync(GuardianHealthCheckDto check, CancellationToken cancellationToken)
    {
        if (check.Type.Equals("process", StringComparison.OrdinalIgnoreCase)) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(check.TimeoutSeconds));
        try
        {
            if (check.Type.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(check.Target!, timeout.Token);
                return response.IsSuccessStatusCode;
            }
            if (check.Type.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(check.Target!);
                using var client = new TcpClient();
                await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                return true;
            }
        }
        catch { /* The result is represented as an unhealthy workload state. */ }
        return false;
    }

    private static async Task CaptureOutputAsync(StreamReader reader, ManagedWorkload workload, string stream)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                workload.Logs.Enqueue(new GuardianLogEntryDto(DateTimeOffset.UtcNow, stream, SanitizeLogLine(line)));
                while (workload.Logs.Count > 500) workload.Logs.TryDequeue(out _);
            }
        }
        catch { /* The process exit path owns final state; logging must not affect supervision. */ }
    }

    private static string SanitizeLogLine(string line)
    {
        var bounded = line.Length <= 4096 ? line : line[..4096];
        return SensitiveValuePattern().Replace(bounded, "${key}=[REDACTED]");
    }

    [GeneratedRegex("(?<key>(?:password|passwd|token|secret|api[_-]?key)\\s*)=\\s*[^\\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveValuePattern();

    private bool Validate(ProcessDefinitionDto definition, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Name) || !Path.IsPathFullyQualified(definition.ExecutablePath) || !File.Exists(definition.ExecutablePath)) { problem = "guardian.validation_executable"; return false; }
        if (!Path.IsPathFullyQualified(definition.WorkingDirectory) || !Directory.Exists(definition.WorkingDirectory)) { problem = "guardian.validation_working_directory"; return false; }
        if (_options.AllowedRoots.Count == 0 || !IsAllowedPath(definition.ExecutablePath) || !IsAllowedPath(definition.WorkingDirectory)) { problem = "guardian.validation_allowed_root"; return false; }
        var executableName = Path.GetFileNameWithoutExtension(definition.ExecutablePath);
        if (executableName.Equals("cmd", StringComparison.OrdinalIgnoreCase) || executableName.Equals("powershell", StringComparison.OrdinalIgnoreCase) || executableName is "sh" or "bash") { problem = "guardian.validation_shell_not_allowed"; return false; }
        if (definition.Arguments.Any(argument => argument.Contains('\0')) || definition.StopTimeoutSeconds is < 1 or > 300 || definition.MaxRestartAttempts is < 0 or > 100 || !ValidateHealthCheck(definition.HealthCheck)) { problem = "guardian.validation_failed"; return false; }
        return true;
    }

    private static bool ValidateHealthCheck(GuardianHealthCheckDto? check)
    {
        if (check is null) return true;
        if (check.IntervalSeconds is < 1 or > 3600 || check.TimeoutSeconds is < 1 or > 60 || check.FailureThreshold is < 1 or > 100) return false;
        if (check.Type.Equals("process", StringComparison.OrdinalIgnoreCase)) return string.IsNullOrWhiteSpace(check.Target);
        if (check.Type.Equals("http", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(check.Target, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        return check.Type.Equals("tcp", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(check.Target, UriKind.Absolute, out var tcpUri) && tcpUri.UserInfo.Length == 0 && tcpUri.Scheme == "tcp" && tcpUri.Port is > 0 and <= 65535;
    }

    private bool IsAllowedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return _options.AllowedRoots.Any(root => fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        await File.WriteAllTextAsync(DefinitionsPath, JsonSerializer.Serialize(_workloads.Values.Select(item => item.Definition), RemoteOsJsonOptions.Default), cancellationToken);
    }

    private async Task WriteAuditAsync(string action, string? workloadId, GuardianAgentResponse response, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        var entry = new GuardianAuditEntryDto(DateTimeOffset.UtcNow, action, workloadId, response.Success ? "success" : "failed", response.ProblemCode);
        await File.AppendAllTextAsync(AuditPath, JsonSerializer.Serialize(entry, RemoteOsJsonOptions.Default) + Environment.NewLine, cancellationToken);
    }

    private async Task<GuardianAgentResponse> ReadAuditAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AuditPath)) return new GuardianAgentResponse(true, string.Empty, Audits: []);
        var lines = await File.ReadAllLinesAsync(AuditPath, cancellationToken);
        var entries = lines.TakeLast(500).Select(line =>
        {
            try { return JsonSerializer.Deserialize<GuardianAuditEntryDto>(line, RemoteOsJsonOptions.Default); }
            catch (JsonException) { return null; }
        }).Where(entry => entry is not null).Select(entry => entry!).ToArray();
        return new GuardianAgentResponse(true, string.Empty, Audits: entries);
    }

    private static GuardianWorkloadDto ToDto(ManagedWorkload workload) => new(workload.Definition.Id, workload.Definition.Name, workload.DesiredState, workload.ActualState, workload.Process is { HasExited: false } process ? process.Id : null, workload.RestartCount, workload.HealthStatus, workload.HealthFailureCount);

    private sealed class ManagedWorkload(ProcessDefinitionDto definition)
    {
        public ProcessDefinitionDto Definition { get; } = definition;
        public Process? Process { get; set; }
        public string DesiredState { get; set; } = "Stopped";
        public string ActualState { get; set; } = "Stopped";
        public int? ExitCode { get; set; }
        public int Generation { get; set; }
        public int RestartCount { get; set; }
        public string HealthStatus { get; set; } = definition.HealthCheck is null ? "NotConfigured" : "Unknown";
        public int HealthFailureCount { get; set; }
        public DateTimeOffset NextHealthCheckAt { get; set; } = DateTimeOffset.UtcNow;
        public ConcurrentQueue<GuardianLogEntryDto> Logs { get; } = new();
    }
}
