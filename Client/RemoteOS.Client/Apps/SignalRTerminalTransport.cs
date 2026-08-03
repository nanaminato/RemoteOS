using Microsoft.AspNetCore.SignalR.Client;
using RemoteOS.Protocol.Hubs;
using RoyalTerminal.Terminal;

namespace Client.Apps;

/// <summary>
/// RoyalTerminal <see cref="ITerminalTransport"/> 的 SignalR 实现：把终端 I/O 桥接到 RemoteOS Server 的
/// Terminal Hub。服务端是 PTY 哑中继——本传输只搬运原始字节，VT 渲染由客户端 <c>TerminalControl</c> 完成。
/// </summary>
public sealed class SignalRTerminalTransport : ITerminalTransport
{
    private readonly SignalRTransportOptions _options;
    private HubConnection? _conn;

    public bool IsRunning { get; private set; }

    // ITerminalTransport events: (buffer, count) 与 exitCode，与服务端 IPty 对齐。
    public event Action<byte[], int>? DataReceived;
    public event Action<int>? ProcessExited;

    public SignalRTerminalTransport(SignalRTransportOptions options) => _options = options;

    public async ValueTask StartAsync(ITerminalTransportOptions options, CancellationToken cancellationToken)
    {
        if (options is not SignalRTransportOptions opts)
            throw new ArgumentException($"Expected {nameof(SignalRTransportOptions)}.", nameof(options));

        _conn = new HubConnectionBuilder()
            .WithUrl(opts.HubUrl, http =>
            {
                http.AccessTokenProvider = () =>
                    Task.FromResult<string?>(opts.TokenProvider?.Invoke() ?? opts.AccessToken);
            })
            .Build();

        _conn.On<byte[]>(TerminalHubEvents.OnOutput, data => DataReceived?.Invoke(data, data.Length));
        _conn.On<int>(TerminalHubEvents.OnProcessExited, code => ProcessExited?.Invoke(code));

        await _conn.StartAsync(cancellationToken);
        await _conn.InvokeAsync(TerminalHubMethods.Start,
            new StartTerminalRequest(
                opts.Dimensions.Columns, opts.Dimensions.Rows,
                opts.Dimensions.WidthPixels, opts.Dimensions.HeightPixels,
                opts.Shell, opts.WorkingDirectory),
            cancellationToken);

        IsRunning = true;
    }

    public void SendInput(ReadOnlySpan<byte> utf8)
    {
        var conn = _conn;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        Fire(conn.InvokeAsync(TerminalHubMethods.Input, utf8.ToArray()));
    }

    public void Resize(TerminalSessionDimensions dimensions)
    {
        var conn = _conn;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        Fire(conn.InvokeAsync(TerminalHubMethods.Resize,
            dimensions.Columns, dimensions.Rows, dimensions.WidthPixels, dimensions.HeightPixels));
    }

    public async ValueTask StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        var conn = _conn;
        if (conn is null) return;
        try { await conn.InvokeAsync(TerminalHubMethods.Close); } catch { /* best effort */ }
        try { await conn.StopAsync(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        IsRunning = false;
        var conn = _conn;
        _conn = null;
        if (conn is not null)
            Fire(conn.DisposeAsync().AsTask());
    }

    /// <summary>观测但不抛出 fire-and-forget 任务的异常（连接断开时 InvokeAsync 可能 fault）。</summary>
    private static void Fire(Task t) =>
        _ = t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
}
