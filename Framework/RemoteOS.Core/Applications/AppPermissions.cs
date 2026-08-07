namespace RemoteOS.Core.Applications;

/// <summary>Capabilities an application can request from the RemoteOS host.</summary>
public static class AppPermissions
{
    /// <summary>Change the RemoteOS desktop wallpaper through the host appearance service.</summary>
    public const string DesktopWallpaperWrite = "desktop.wallpaper.write";
    /// <summary>Read aggregate, non-process server resource metrics through the host monitor service.</summary>
    public const string ServerMetricsRead = "server.metrics.read";

    public static IReadOnlyList<AppPermissionDefinition> All { get; } =
    [
        new(DesktopWallpaperWrite, "修改桌面壁纸", "允许此应用通过 RemoteOS 修改当前工作区的桌面壁纸。"),
        new(ServerMetricsRead, "读取服务器性能指标", "允许此应用读取服务器 CPU、内存、磁盘、网络和 GPU 的聚合性能数据。"),
    ];

    public static AppPermissionDefinition? Find(string? permissionId) =>
        All.FirstOrDefault(permission => string.Equals(permission.Id, permissionId, StringComparison.Ordinal));

    public static bool IsKnown(string? permissionId) => Find(permissionId) is not null;
}

/// <summary>Metadata shown to users when an application asks for a host capability.</summary>
public sealed record AppPermissionDefinition(string Id, string DisplayName, string Description);
