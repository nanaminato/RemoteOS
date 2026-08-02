using System.Text.Json.Serialization;
using RemoteOS.Protocol.Common;

namespace RemoteOS.Protocol.Identity;

/// <summary>RemoteOS 用户身份。对应 Authentication.md §10 users 表。RemoteOS 不保存宿主 OS 密码，认证委托宿主 OS。</summary>
public sealed record UserDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("platform")] PlatformKind Platform,
    [property: JsonPropertyName("platformIdentity")] string PlatformIdentity,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastLoginAt")] DateTimeOffset? LastLoginAt);
