namespace RemoteOS.Protocol.Docker;

/// <summary>Non-executing runtime installation/startup plan. Host elevation remains outside RemoteOS.</summary>
public sealed record DockerInstallationPlanDto(bool CanProceed, string ProblemCode, IReadOnlyList<string> Steps, IReadOnlyList<string> Warnings);
public sealed record DockerInstallationExecutionRequest(bool Confirmed);
