using RemoteOS.Protocol.Registry;

namespace Client.Apps.Registry;

/// <summary>Host-only client for the schema-approved registry read model.</summary>
public interface IRegistryClient
{
    Task<IReadOnlyList<RegistryEntryDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<RegistrySummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
}
