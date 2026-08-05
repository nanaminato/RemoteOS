using RemoteOS.Protocol.SystemMonitor;

namespace Client.Apps.TaskManager;

/// <summary>RemoteOS Server 系统监控 HTTP 客户端抽象。typed HttpClient 实现（见 <see cref="TaskManagerClient"/>）。
/// 所有方法从 <c>IAuthSession</c> 取 <c>serverUrl</c> + <c>accessToken</c> 构造绝对 URI 与 Authorization 头。
/// 路由常量见 <see cref="SystemMonitorApiRoutes"/>。错误统一为 <see cref="RemoteOsAuthException"/>（含 ProblemDetails）。</summary>
public interface ITaskManagerClient
{
    /// <summary>获取整机资源占用快照（CPU/内存/磁盘/网络/GPU/运行时间）。</summary>
    Task<SystemMetricsDto> GetMetricsAsync(CancellationToken ct = default);

    /// <summary>列举当前可见进程（含每进程 CPU% 与内存）。</summary>
    Task<IReadOnlyList<ProcessInfoDto>> ListProcessesAsync(CancellationToken ct = default);

    /// <summary>结束指定进程。force=true 强制终止。权限不足返回 RequiresElevation=true。</summary>
    Task<KillProcessResultDto> KillProcessAsync(int processId, bool force = false, CancellationToken ct = default);
}
