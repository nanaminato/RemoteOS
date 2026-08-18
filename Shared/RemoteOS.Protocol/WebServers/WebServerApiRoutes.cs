using RemoteOS.Protocol.Common;

namespace RemoteOS.Protocol.WebServers;

/// <summary>Host-global Web Server API routes. Consumers must not duplicate these strings.</summary>
public static class WebServerApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
    public const string WebServers = $"/{V1}/webservers";
    public const string CollectionPattern = "";
    public const string Discover = $"{WebServers}/discover";
    public const string DiscoverPattern = "/discover";
    public const string ById = $"{WebServers}/{{id}}";
    public const string ByIdPattern = "/{id}";
    public const string Status = $"{WebServers}/{{id}}/status";
    public const string StatusPattern = "/{id}/status";
    public const string TestConfiguration = $"{WebServers}/{{id}}/config/test";
    public const string TestConfigurationPattern = "/{id}/config/test";
    public const string Integrate = $"{WebServers}/{{id}}/integrate";
    public const string IntegratePattern = "/{id}/integrate";
    public const string ManagedInstall = $"{WebServers}/managed/{{providerId}}/install";
    public const string ManagedInstallPattern = "/managed/{providerId}/install";
    public const string Lifecycle = $"{WebServers}/{{id}}/lifecycle/{{action}}";
    public const string LifecyclePattern = "/{id}/lifecycle/{action}";
    public const string ManagedUninstall = $"{WebServers}/{{id}}/managed/uninstall";
    public const string ManagedUninstallPattern = "/{id}/managed/uninstall";
    public const string Reload = $"{WebServers}/{{id}}/reload";
    public const string ReloadPattern = "/{id}/reload";
    public const string Operations = $"{WebServers}/operations/{{operationId}}";
    public const string OperationsPattern = "/operations/{operationId:guid}";
    public const string CancelOperation = $"{WebServers}/operations/{{operationId}}/cancel";
    public const string CancelOperationPattern = "/operations/{operationId:guid}/cancel";
}
