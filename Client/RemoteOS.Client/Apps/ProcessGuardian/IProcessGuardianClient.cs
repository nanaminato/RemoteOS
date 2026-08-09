using RemoteOS.Protocol.ProcessGuardian;

namespace Client.Apps.ProcessGuardian;

public interface IProcessGuardianClient
{
    Task<GuardianStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianWorkloadDto>> ListWorkloadsAsync(CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> UpsertAsync(ProcessDefinitionDto definition, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> ApplyActionAsync(string id, string action, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken = default);
}
