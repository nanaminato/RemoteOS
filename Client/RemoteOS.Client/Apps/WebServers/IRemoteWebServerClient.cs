using RemoteOS.Protocol.WebServers;

namespace Client.Apps.WebServers;

/// <summary>
/// Client-side facade for the host-global web server API. Discovery and read are stateless;
/// integrate/reload start long-running operations tracked by an operation id.
/// </summary>
public interface IRemoteWebServerClient
{
    Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<WebServerStatusDto?> GetStatusAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> IntegrateAsync(string id, IntegrateWebServerRequest request, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> ReloadAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
}
