namespace RemoteOS.Core.Applications;

/// <summary>Capabilities an application can request from the RemoteOS host.</summary>
public static class AppPermissions
{
    public const string ServerFilesRead = "server.files.read";
    public const string ServerFilesWrite = "server.files.write";
    public const string DesktopWallpaperWrite = "desktop.wallpaper.write";
    public const string ServerMetricsRead = "server.metrics.read";
    public const string ServerProcessesManage = "server.processes.manage";
    public const string ServerServicesManage = "server.services.manage";
    public const string ServerNetworkRead = "server.network.read";
    public const string ServerNetworkConfigure = "server.network.configure";
    public const string ServerPowerManage = "server.power.manage";
    public const string ServerDockerRead = "server.docker.read";
    public const string ServerDockerManage = "server.docker.manage";
    public const string ServerGuardianRead = "server.guardian.read";
    public const string ServerGuardianManage = "server.guardian.manage";
    public const string ServerFirewallRead = "server.firewall.read";
    public const string ServerFirewallManage = "server.firewall.manage";
    public const string ServerCertificatesRead = "server.certificates.read";
    public const string ServerCertificatesManage = "server.certificates.manage";
    public const string ServerWebServersRead = "server.webservers.read";
    public const string ServerWebServersManage = "server.webservers.manage";
    public const string ServerGitRead = "server.git.read";
    public const string ServerGitManage = "server.git.manage";

    /// <summary>
    /// English source metadata for every capability. Client UI resolves each value through
    /// <c>permission.{id}.*</c>; these values remain the SDK-safe English fallback.
    /// </summary>
    public static IReadOnlyList<AppPermissionDefinition> All { get; } =
    [
        new(ServerFilesRead, "Read server files", "Read files, directory structure, and basic metadata on the server.", "server_files"),
        new(ServerFilesWrite, "Modify server files", "Create, upload, edit, rename, and delete server files. This does not include changing file ownership or system permissions.", "server_files"),
        new(ServerProcessesManage, "Manage server processes", "View, start, stop, or terminate server processes. This permission includes reading the process list.", "server_management"),
        new(ServerServicesManage, "Manage system services", "View, start, stop, or restart services on the server.", "server_management"),
        new(ServerNetworkRead, "Read server network information", "Read network interfaces, IP addresses, routes, and connection state.", "server_network"),
        new(ServerNetworkConfigure, "Configure server network", "Change server IP addresses, routes, DNS, and other network configuration. It does not grant unrelated management capabilities.", "server_network"),
        new(ServerPowerManage, "Perform server power operations", "Request high-impact server operations such as restart or shutdown.", "server_management"),
        new(DesktopWallpaperWrite, "Change desktop wallpaper", "Allow this application to change the current workspace wallpaper through RemoteOS.", "desktop_workspace"),
        new(ServerMetricsRead, "Read server performance metrics", "Read aggregate CPU, memory, disk, network, and GPU metrics from the server.", "server_monitoring"),
        new(ServerDockerRead, "Read Docker resources", "View the server's local Docker status, containers, images, networks, volumes, and safe diagnostics.", "server_management"),
        new(ServerDockerManage, "Manage Docker resources", "Create and change local Docker containers, images, and Compose stacks on the server.", "server_management"),
        new(ServerGuardianRead, "Read guardian workloads", "View the Guardian Agent, its workloads, states, and sanitized logs.", "server_management"),
        new(ServerGuardianManage, "Manage guardian workloads", "Create and control workloads supervised by the Guardian Agent.", "server_management"),
        new(ServerFirewallRead, "Read firewall configuration", "View the Linux server firewall status, defaults, and rules.", "server_network"),
        new(ServerFirewallManage, "Manage firewall configuration", "Change the Linux server firewall status, default policies, and rules.", "server_network"),
        new(ServerCertificatesRead, "Read TLS certificates", "View TLS/SSL certificate metadata, issuance status, and renewal state on the server.", "server_management"),
        new(ServerCertificatesManage, "Manage TLS certificates", "Request, renew, deploy, revoke, and delete TLS certificates on the server.", "server_management"),
        new(ServerWebServersRead, "Read web server configuration", "View discovered web servers, their runtime status, and configuration metadata on the server.", "server_management"),
        new(ServerWebServersManage, "Manage web server configuration", "Integrate, reload, and test web server configuration on the server.", "server_management"),
        new(ServerGitRead, "Read Git repositories", "View registered Git repositories, their status, branches, history, and diffs on the server.", "server_files"),
        new(ServerGitManage, "Manage Git repositories", "Commit, pull, push, switch, create and delete branches, and revert commits in registered Git repositories on the server.", "server_files"),
    ];

    public static AppPermissionDefinition? Find(string? permissionId) =>
        All.FirstOrDefault(permission => string.Equals(permission.Id, permissionId, StringComparison.Ordinal));

    public static bool IsKnown(string? permissionId) => Find(permissionId) is not null;

    /// <summary>Returns a stable category identifier for client-side localization.</summary>
    public static string GetCategory(AppPermissionDefinition permission) => permission.Category;
}

/// <summary>English fallback metadata for a host capability requested by an application.</summary>
public sealed record AppPermissionDefinition(string Id, string DisplayName, string Description, string Category = "other");
