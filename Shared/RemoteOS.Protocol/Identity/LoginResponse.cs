using System.Text.Json.Serialization;
using RemoteOS.Protocol.Workspace;

namespace RemoteOS.Protocol.Identity;

/// <summary>登录成功响应。一次性返回 User/Workspace/Session/Device/Tokens/角色，Client 据此建立 SignalR 连接。</summary>
public sealed record LoginResponse(
    [property: JsonPropertyName("user")] UserDto User,
    [property: JsonPropertyName("workspace")] WorkspaceDto Workspace,
    [property: JsonPropertyName("session")] SessionDto Session,
    [property: JsonPropertyName("device")] DeviceDto Device,
    [property: JsonPropertyName("tokens")] AuthTokens Tokens,
    [property: JsonPropertyName("assignedRole")] DeviceRole AssignedRole);
