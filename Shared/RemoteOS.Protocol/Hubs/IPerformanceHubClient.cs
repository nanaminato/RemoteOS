using RemoteOS.Protocol.SystemMonitor;

namespace RemoteOS.Protocol.Hubs;

/// <summary>性能 Hub 的 server→client 事件契约。</summary>
public interface IPerformanceHubClient
{
    Task OnPerformanceSnapshot(PerformanceRealtimeSnapshotDto snapshot);
}
