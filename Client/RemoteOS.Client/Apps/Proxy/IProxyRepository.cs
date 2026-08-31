using RemoteOS.Protocol.Proxy;

namespace Client.Apps.Proxy;

public interface IProxyRepository
{
    Task<ProxyOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> LifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> EmergencyDisableTunAsync(CancellationToken cancellationToken = default);
}
