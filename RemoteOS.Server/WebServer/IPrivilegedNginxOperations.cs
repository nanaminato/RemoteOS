using RemoteOS.Protocol.Privileged;

namespace Server.WebServer;

/// <summary>Closed Nginx package and system-service operations; no executable or argument API.</summary>
public interface IPrivilegedNginxOperations
{
    Task<bool> ApplySystemServiceActionAsync(NginxSystemServiceAction action, CancellationToken cancellationToken = default);
    Task<bool> InstallPackageAsync(string? version, CancellationToken cancellationToken = default);
    Task<bool> UninstallPackageAsync(CancellationToken cancellationToken = default);
}

public sealed class PrivilegedNginxOperations(Server.Privileged.IPrivilegedOperationTransport transport) : IPrivilegedNginxOperations
{
    public Task<bool> ApplySystemServiceActionAsync(NginxSystemServiceAction action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxSystemServiceAction, NginxServiceAction: action), cancellationToken);
    public Task<bool> InstallPackageAsync(string? version, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxPackageInstall, PackageVersion: version), cancellationToken);
    public Task<bool> UninstallPackageAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxPackageUninstall), cancellationToken);

    private async Task<bool> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken) =>
        (await transport.ExecuteAsync(request, cancellationToken)).Success;
}
