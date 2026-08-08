using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using RemoteOS.Protocol.SystemMonitor;

namespace Server.Endpoints;

/// <summary>系统监控（任务管理器）REST 端点。路由常量见 <see cref="SystemMonitorApiRoutes"/>。所有端点需 JWT（[Authorize]）。
/// 数据采集由 <see cref="Server.SystemMonitor.ISystemMetricsProvider"/> 完成（以宿主 OS 进程身份，复用宿主用户/权限）。
/// 错误统一返回 RFC 7807 ProblemDetails（type URI 作错误码）。</summary>
public static class SystemMonitorEndpoints
{
    private const string ProblemBase = "https://remoteos.app/problems/";

    public static IEndpointRouteBuilder MapSystemMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        // GET system/metrics — 整机资源占用快照（CPU/内存/磁盘/网络/GPU/运行时间）
        app.MapGet(SystemMonitorApiRoutes.Metrics,
            (Server.SystemMonitor.ISystemMetricsProvider provider, CancellationToken ct)
                => provider.GetMetricsAsync(ct))
           .RequireAuthorization()
           .WithTags("System");

        app.MapGet(SystemMonitorApiRoutes.NetworkAddresses, () => Results.Ok(GetNetworkAddresses()))
           .RequireAuthorization()
           .WithTags("System");

        // GET system/processes — 当前可见进程列表（每进程 CPU% / 内存 / 属主）
        app.MapGet(SystemMonitorApiRoutes.Processes,
            (Server.SystemMonitor.ISystemMetricsProvider provider, CancellationToken ct)
                => provider.ListProcessesAsync(ct))
           .RequireAuthorization()
           .WithTags("System");

        // DELETE system/processes/{id}?force= — 结束进程；权限不足返回 requiresElevation
        app.MapDelete(SystemMonitorApiRoutes.ProcessKill,
            (int id, bool? force, Server.SystemMonitor.ISystemMetricsProvider provider, CancellationToken ct)
                => provider.KillProcessAsync(id, force ?? false, ct))
           .RequireAuthorization()
           .WithTags("System");

        return app;
    }

    private static IReadOnlyList<NetworkAddressDto> GetNetworkAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                                  network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses
                    .Select(unicast => (network.Name, Address: unicast.Address)))
                .Where(item => item.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(item => !IPAddress.IsLoopback(item.Address) && !item.Address.IsIPv6LinkLocal)
                .Select(item => new NetworkAddressDto(
                    item.Name,
                    item.Address.ToString(),
                    item.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6"))
                .DistinctBy(item => (item.Family, item.Address))
                .OrderBy(item => item.Family)
                .ThenBy(item => item.Address, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<NetworkAddressDto>();
        }
    }
}
