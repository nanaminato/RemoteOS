using System.Text.Json.Serialization;
using RemoteOS.Protocol.Common;

namespace RemoteOS.Protocol.Identity;

/// <summary>登录请求。密码由 Server 委托宿主 OS 验证（Linux: PAM / Windows: LogonUser），RemoteOS 不存密码。</summary>
public sealed record LoginRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("clientPlatform")] PlatformKind ClientPlatform,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("clientVersion")] string ClientVersion);
