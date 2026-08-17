using RemoteOS.Protocol.WebServers;

namespace Server.WebServer;

public interface IWebServerManager
{
    Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken);
    Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken);
    Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken);
    Task<WebServerOperationDto?> IntegrateAsync(string instanceId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken);
    Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken);
}
