using Microsoft.AspNetCore.SignalR.Client;
using RemoteOS.Protocol.Hubs;

namespace Client.Apps.Terminal;

/// <summary>
/// 构建终端 Hub 的 <see cref="HubConnection"/>（未启动）。传输层与"拉取会话列表"的临时查询连接共用，
/// 保证鉴权（AccessTokenProvider）与 URL 拼装一致。
/// </summary>
public static class TerminalHubConnection
{
    public static HubConnection Build(SignalRTransportOptions opts)
    {
        // 不启用 WithAutomaticReconnect：自动重连后服务端不会自动重新附加会话，会进入半附加状态。
        // 恢复路径是"再次登录打开终端"→重新 Start(Attach)→服务端回放缓冲快照，而非进程内自动重连。
        return new HubConnectionBuilder()
            .WithUrl(opts.HubUrl, http =>
            {
                http.AccessTokenProvider = () =>
                    Task.FromResult<string?>(opts.TokenProvider?.Invoke() ?? opts.AccessToken);
            })
            .Build();
    }

    /// <summary>
    /// 用一次性连接拉取当前用户的终端会话列表（附加前调用，决定恢复哪个会话或新建）。
    /// 连接用完即释放，不复用传输层连接。
    /// </summary>
    public static async Task<List<TerminalSessionInfo>> ListSessionsAsync(
        SignalRTransportOptions opts, CancellationToken ct = default)
    {
        var conn = Build(opts);
        try
        {
            await conn.StartAsync(ct).ConfigureAwait(false);
            return await conn.InvokeAsync<List<TerminalSessionInfo>>(
                TerminalHubMethods.ListSessions, ct).ConfigureAwait(false) ?? new();
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }
}

