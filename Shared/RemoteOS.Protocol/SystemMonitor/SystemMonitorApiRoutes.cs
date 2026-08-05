using RemoteOS.Protocol.Common;

namespace RemoteOS.Protocol.SystemMonitor;

/// <summary>系统监控（任务管理器）REST 端点路由常量。路径已含 /api/v1 前缀。Server 注册路由与 Client 拼接 URL 共用。
/// 所有端点需 JWT（[Authorize]）。错误统一返回 RFC 7807 ProblemDetails。</summary>
public static class SystemMonitorApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;

    /// <summary>获取整机资源占用快照（GET，需 JWT）。返回 SystemMetricsDto。</summary>
    public const string Metrics = $"/{V1}/system/metrics";

    /// <summary>列举进程（GET，需 JWT）。返回 ProcessInfoDto[]。query: includeAll（可选，默认仅当前可见）。</summary>
    public const string Processes = $"/{V1}/system/processes";

    /// <summary>结束进程（DELETE，需 JWT）。route: id。返回 KillProcessResultDto。</summary>
    public const string ProcessKill = $"/{V1}/system/processes/{{id}}";
}
