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
    ];

    public static AppPermissionDefinition? Find(string? permissionId) =>
        All.FirstOrDefault(permission => string.Equals(permission.Id, permissionId, StringComparison.Ordinal));

    public static bool IsKnown(string? permissionId) => Find(permissionId) is not null;

    /// <summary>Returns a stable category identifier for client-side localization.</summary>
    public static string GetCategory(AppPermissionDefinition permission) => permission.Category;
}

/// <summary>English fallback metadata for a host capability requested by an application.</summary>
public sealed record AppPermissionDefinition(string Id, string DisplayName, string Description, string Category = "other");
