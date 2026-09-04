using RemoteOS.Protocol.Privileged;

namespace Server.WebServer;

/// <summary>Closed Nginx package and system-service operations; no executable or argument API.</summary>
public interface IPrivilegedNginxOperations
{
    Task<bool> ApplySystemServiceActionAsync(NginxSystemServiceAction action, CancellationToken cancellationToken = default);
    Task<bool> InstallPackageAsync(string? version, CancellationToken cancellationToken = default);
    Task<bool> UninstallPackageAsync(CancellationToken cancellationToken = default);
    Task<bool> WriteManagedFileAsync(string path, byte[] content, CancellationToken cancellationToken = default);
    Task<bool> MoveManagedFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<bool> DeleteManagedFileAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class PrivilegedNginxOperations(Server.Privileged.IPrivilegedOperationTransport transport) : IPrivilegedNginxOperations
{
    public Task<bool> ApplySystemServiceActionAsync(NginxSystemServiceAction action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxSystemServiceAction, NginxServiceAction: action), cancellationToken);
    public Task<bool> InstallPackageAsync(string? version, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxPackageInstall, PackageVersion: version), cancellationToken);
    public Task<bool> UninstallPackageAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxPackageUninstall), cancellationToken);
    public Task<bool> WriteManagedFileAsync(string path, byte[] content, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxWriteManagedFile, Path: path, ContentBase64: Convert.ToBase64String(content)), cancellationToken);
    public Task<bool> MoveManagedFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxMoveManagedFile, Path: sourcePath, DestinationPath: destinationPath, Overwrite: overwrite), cancellationToken);
    public Task<bool> DeleteManagedFileAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxDeleteManagedFile, Path: path), cancellationToken);

    private async Task<bool> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken) =>
        (await transport.ExecuteAsync(request, cancellationToken)).Success;
}
