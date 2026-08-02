using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Identity;

/// <summary>刷新令牌响应。</summary>
public sealed record RefreshTokenResponse(
    [property: JsonPropertyName("tokens")] AuthTokens Tokens);
