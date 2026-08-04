using RemoteOS.Protocol.Common;

namespace RemoteOS.Protocol.Workspace;

/// <summary>Workspace 相关 REST 端点路由常量。路径已含 /api/v1 前缀。Server 注册路由与 Client 拼接 URL 共用。含 {id} 占位符的常量格式化时替换。</summary>
public static class WorkspaceApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;

    /// <summary>当前用户的所有 Workspace（GET）。</summary>
    public const string List = $"/{V1}/workspaces";

    /// <summary>创建 Workspace（POST）。</summary>
    public const string Create = $"/{V1}/workspaces";

    /// <summary>单个 Workspace（GET）。{id} 为 Workspace ID。</summary>
    public const string GetById = $"/{V1}/workspaces/{{id}}";

    /// <summary>Workspace 的 Session 列表（GET）。{id} 为 Workspace ID。</summary>
    public const string Sessions = $"/{V1}/workspaces/{{id}}/sessions";

    /// <summary>Workspace 当前在线设备列表（GET）。{id} 为 Workspace ID。</summary>
    public const string Devices = $"/{V1}/workspaces/{{id}}/devices";

    /// <summary>Workspace 桌面状态全量快照（GET） / 更新（PUT，仅 Controller）。{id} 为 Workspace ID。</summary>
    public const string Desktop = $"/{V1}/workspaces/{{id}}/desktop";

    /// <summary>Workspace terminal appearance preferences (GET/PUT).</summary>
    public const string TerminalSettings = $"/{V1}/workspaces/{{id}}/terminal-settings";

    /// <summary>请求控制权（POST）。{id} 为 Workspace ID。</summary>
    public const string RequestControl = $"/{V1}/workspaces/{{id}}/control/request";

    /// <summary>释放控制权（POST）。{id} 为 Workspace ID。</summary>
    public const string ReleaseControl = $"/{V1}/workspaces/{{id}}/control/release";

    /// <summary>显式注册设备（POST）。</summary>
    public const string RegisterDevice = $"/{V1}/devices";
}
