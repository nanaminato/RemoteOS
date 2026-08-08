namespace RemoteOS.Core.Applications;

/// <summary>Capabilities an application can request from the RemoteOS host.</summary>
public static class AppPermissions
{
    /// <summary>Read server files and directory metadata through a host-provided file capability.</summary>
    public const string ServerFilesRead = "server.files.read";

    /// <summary>Create, modify, rename, or delete server files through a host-provided file capability.</summary>
    public const string ServerFilesWrite = "server.files.write";

    /// <summary>Change the RemoteOS desktop wallpaper through the host appearance service.</summary>
    public const string DesktopWallpaperWrite = "desktop.wallpaper.write";
    /// <summary>Read aggregate, non-process server resource metrics through the host monitor service.</summary>
    public const string ServerMetricsRead = "server.metrics.read";

    /// <summary>Inspect, start, stop, or terminate server processes through a host-provided process capability.</summary>
    public const string ServerProcessesManage = "server.processes.manage";

    /// <summary>Inspect, start, stop, or restart system services through a host-provided service capability.</summary>
    public const string ServerServicesManage = "server.services.manage";

    /// <summary>Read server network interfaces, addresses, and connection information.</summary>
    public const string ServerNetworkRead = "server.network.read";

    /// <summary>Change server network configuration, including addresses, routes, and DNS settings.</summary>
    public const string ServerNetworkConfigure = "server.network.configure";

    /// <summary>Request host-mediated server power operations such as restart or shutdown.</summary>
    public const string ServerPowerManage = "server.power.manage";

    public static IReadOnlyList<AppPermissionDefinition> All { get; } =
    [
        new(ServerFilesRead, "读取服务器文件", "允许读取服务器上的文件、目录结构和基本元数据。", "服务器文件"),
        new(ServerFilesWrite, "修改服务器文件", "允许创建、上传、修改、重命名和删除服务器文件；不包含更改文件所有权或系统权限。", "服务器文件"),
        new(ServerProcessesManage, "管理服务器进程", "允许查看并启动、停止或终止服务器进程。此权限包含进程列表读取，以降低授权复杂度。", "服务器管理"),
        new(ServerServicesManage, "管理系统服务", "允许查看并启动、停止或重启服务器服务。", "服务器管理"),
        new(ServerNetworkRead, "读取服务器网络信息", "允许读取网络接口、IP 地址、路由和连接状态。", "服务器网络"),
        new(ServerNetworkConfigure, "配置服务器网络", "允许修改 IP 地址、路由和 DNS 等服务器网络配置；该权限不自动授予读取以外的管理能力。", "服务器网络"),
        new(ServerPowerManage, "执行服务器电源操作", "允许请求服务器重启或关机等高影响操作。", "服务器管理"),
        new(DesktopWallpaperWrite, "修改桌面壁纸", "允许此应用通过 RemoteOS 修改当前工作区的桌面壁纸。"),
        new(ServerMetricsRead, "读取服务器性能指标", "允许此应用读取服务器 CPU、内存、磁盘、网络和 GPU 的聚合性能数据。"),
    ];

    public static AppPermissionDefinition? Find(string? permissionId) =>
        All.FirstOrDefault(permission => string.Equals(permission.Id, permissionId, StringComparison.Ordinal));

    public static bool IsKnown(string? permissionId) => Find(permissionId) is not null;

    /// <summary>Returns the user-facing category for a known permission.</summary>
    public static string GetCategory(AppPermissionDefinition permission) =>
        permission.Category == "其他"
            ? permission.Id switch
            {
                DesktopWallpaperWrite => "桌面与工作区",
                ServerMetricsRead => "服务器监控",
                _ => permission.Category,
            }
            : permission.Category;
}

/// <summary>Metadata shown to users when an application asks for a host capability.</summary>
public sealed record AppPermissionDefinition(string Id, string DisplayName, string Description, string Category = "其他");
