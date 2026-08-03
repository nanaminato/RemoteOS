using System.Collections;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RemoteOS.Protocol.Hubs;
using RoyalTerminal.Terminal;

namespace Server.Hubs;

/// <summary>
/// Remote Terminal Hub —— 服务端 PTY 哑中继。每条 Hub 连接对应一个 <see cref="IPty"/>：
/// <see cref="Start"/> 创建 PTY 并 spawn shell，<see cref="Input"/> 写入用户输入，
/// PTY 输出原样回传 <see cref="ITerminalHubClient.OnOutput"/>。VT 解析（标题/响铃/光标）全部在客户端完成。
/// 连接断开（<see cref="OnDisconnectedAsync"/>）即停止并释放 PTY，确保无残留 shell 进程。
/// </summary>
[Authorize]
public sealed class TerminalHub : Hub<ITerminalHubClient>
{
    private const string PtyKey = "pty";

    private readonly IPtyFactory _ptyFactory;

    public TerminalHub(IPtyFactory ptyFactory) => _ptyFactory = ptyFactory;

    /// <summary>启动远端 PTY 会话。方法名 <c>Start</c> 与 <see cref="TerminalHubMethods.Start"/> 对齐。</summary>
    public Task Start(StartTerminalRequest req)
    {
        // SignalR disposes the hub instance after each method invocation; the PTY
        // DataReceived/ProcessExited callbacks run on background threads and must not
        // touch this.Clients / this.Context (ObjectDisposedException). Capture the
        // connection-scoped client proxy and items dictionary while the hub is alive.
        var caller = Clients.Caller;
        var items = Context.Items;
        // 同一连接重复 Start：先释放旧 PTY（容错）。
        DisposePty(items);

        var pty = _ptyFactory.Create();
        items[PtyKey] = pty;

        // IPty.DataReceived delivers (buffer, count): the buffer may be larger than count.
        pty.DataReceived += (buffer, count) =>
        {
            if (count <= 0) return;
            var data = count == buffer.Length ? buffer : buffer[..count];
            try { _ = caller.OnOutput(data); } catch { /* client disconnected; ignore */ }
        };
        pty.ProcessExited += code =>
        {
            try { _ = caller.OnProcessExited(code); } catch { /* client disconnected; ignore */ }
            DisposePty(items);
        };

        var shell = string.IsNullOrWhiteSpace(req.Shell) ? DefaultShell() : req.Shell!;
        var workingDirectory = string.IsNullOrWhiteSpace(req.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : req.WorkingDirectory!;

        try
        {
            pty.Start(shell, req.Columns, req.Rows, workingDirectory, BuildEnvironment(), null);
        }
        catch
        {
            // Leave no half-started PTY behind if the shell fails to spawn.
            DisposePty(items);
            throw;
        }
        return Task.CompletedTask;
    }

    /// <summary>向 PTY 写入用户输入字节。</summary>
    public Task Input(byte[] data)
    {
        if (GetPty() is { } pty && data.Length > 0)
            pty.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    /// <summary>调整 PTY 尺寸。</summary>
    public Task Resize(int columns, int rows, int widthPixels, int heightPixels)
    {
        if (GetPty() is { } pty)
            pty.Resize(columns, rows, widthPixels, heightPixels);
        return Task.CompletedTask;
    }

    /// <summary>关闭并释放 PTY（不关闭连接，允许客户端重新 Start）。</summary>
    public Task Close()
    {
        DisposePty();
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        DisposePty();
        return base.OnDisconnectedAsync(exception);
    }

    private IPty? GetPty() =>
        Context.Items.TryGetValue(PtyKey, out var p) ? p as IPty : null;

    private void DisposePty(IDictionary<object, object?>? items = null)
    {
        // ProcessExited fires from a background thread; this.Context is unavailable once
        // the hub instance is disposed, so use the captured items dictionary instead.
        var dict = items ?? Context.Items;
        if (!dict.Remove(PtyKey, out var p) || p is not IPty pty)
            return;
        try { pty.Stop(); } catch { /* best effort */ }
        (pty as IDisposable)?.Dispose();
    }

    private static string DefaultShell() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "bash";

    /// <summary>继承宿主进程环境并补一个终端类型，保证 shell 有 PATH 等基础变量。</summary>
    private static Dictionary<string, string> BuildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
            if (kv.Key is string k && kv.Value is string v)
                env[k] = v;
        env["TERM"] = "xterm-256color";
        return env;
    }
}
