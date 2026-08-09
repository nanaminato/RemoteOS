namespace RemoteOS.Protocol.ProcessGuardian;

/// <summary>Reports only the independently installed Guardian Agent, never an in-process substitute.</summary>
public sealed record GuardianStatusDto(bool IsInstalled, bool IsRunning, string ProblemCode, string? Version);

/// <summary>Read-only workload snapshot supplied by the Guardian Agent.</summary>
public sealed record GuardianWorkloadDto(
    string Id,
    string Name,
    string DesiredState,
    string ActualState,
    int? ProcessId,
    int RestartCount);
public sealed record GuardianLogEntryDto(DateTimeOffset Timestamp, string Stream, string Message);

/// <summary>Versioned, shell-free workload declaration accepted by the Guardian Agent.</summary>
public sealed record ProcessDefinitionDto(
    string Id,
    string Name,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool EnabledOnBoot = false,
    int StopTimeoutSeconds = 15,
    int MaxRestartAttempts = 3);

/// <summary>Private local IPC envelope. It is never exposed through RemoteOS HTTP endpoints.</summary>
public sealed record GuardianAgentRequest(string SharedSecret, string Command, string? WorkloadId = null, ProcessDefinitionDto? Definition = null);
public sealed record GuardianAgentResponse(bool Success, string ProblemCode, GuardianStatusDto? Status = null, IReadOnlyList<GuardianWorkloadDto>? Workloads = null, IReadOnlyList<GuardianLogEntryDto>? Logs = null);
