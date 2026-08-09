using RemoteOS.Protocol.Common;

namespace RemoteOS.Protocol.ProcessGuardian;

/// <summary>Routes for the RemoteOS Guardian Agent facade.</summary>
public static class ProcessGuardianApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
    public const string Status = $"/{V1}/guardian/status";
    public const string Workloads = $"/{V1}/guardian/workloads";
    public const string WorkloadAction = $"/{V1}/guardian/workloads/{{id}}/{{action}}";
    public const string WorkloadLogs = $"/{V1}/guardian/workloads/{{id}}/logs";
}
