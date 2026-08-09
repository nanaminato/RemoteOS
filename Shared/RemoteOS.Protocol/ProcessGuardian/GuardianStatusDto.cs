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
    int RestartCount,
    string? HealthStatus = null,
    int HealthFailureCount = 0);
public sealed record GuardianLogEntryDto(DateTimeOffset Timestamp, string Stream, string Message);
public sealed record GuardianAuditEntryDto(DateTimeOffset Timestamp, string Action, string? WorkloadId, string Outcome, string ProblemCode);
public sealed record GuardianHealthCheckDto(string Type, string? Target = null, int IntervalSeconds = 15, int TimeoutSeconds = 5, int FailureThreshold = 3);
public sealed record NativeServiceDto(string Id, string DisplayName, string Status, string StartMode, string Platform);
public sealed record NativeServiceActionRequest(bool Confirmed);
public sealed record GuardianInstallationPlanDto(bool CanProceed, string ProblemCode, IReadOnlyList<string> Steps, IReadOnlyList<string> Warnings);
public sealed record GuardianInstallationExecutionRequest(bool Confirmed);
public sealed record GuardianOperationResult(bool Success, string ProblemCode);

/// <summary>Versioned, shell-free workload declaration accepted by the Guardian Agent.</summary>
public sealed record ProcessDefinitionDto(
    string Id,
    string Name,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool EnabledOnBoot = false,
    int StopTimeoutSeconds = 15,
    int MaxRestartAttempts = 3,
    GuardianHealthCheckDto? HealthCheck = null);

/// <summary>Private local IPC envelope. It is never exposed through RemoteOS HTTP endpoints.</summary>
public sealed record GuardianAgentRequest(string SharedSecret, string Command, string? WorkloadId = null, ProcessDefinitionDto? Definition = null);
public sealed record GuardianAgentResponse(bool Success, string ProblemCode, GuardianStatusDto? Status = null, IReadOnlyList<GuardianWorkloadDto>? Workloads = null, IReadOnlyList<GuardianLogEntryDto>? Logs = null, IReadOnlyList<GuardianAuditEntryDto>? Audits = null);
