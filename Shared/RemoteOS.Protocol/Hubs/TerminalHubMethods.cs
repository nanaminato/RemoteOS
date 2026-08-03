namespace RemoteOS.Protocol.Hubs;

/// <summary>Terminal Hub 的 client→server invoke 方法名常量。Client 端 HubConnection.InvokeAsync 用。</summary>
public static class TerminalHubMethods
{
    /// <summary>启动远端 PTY 会话（创建 PTY、spawn shell）。返回前 PTY 已 Start。</summary>
    public const string Start = nameof(Start);

    /// <summary>向 PTY 写入用户输入字节（client→server）。</summary>
    public const string Input = nameof(Input);

    /// <summary>调整 PTY 尺寸（列/行/像素）。</summary>
    public const string Resize = nameof(Resize);

    /// <summary>关闭并释放 PTY。</summary>
    public const string Close = nameof(Close);
}
