namespace Server.Identity;

/// <summary>JWT 配置。绑定 appsettings.json 的 "Jwt" 节。Secret 至少 32 字符（HMACSHA256 要求 ≥256 位）。</summary>
public sealed class JwtOptions
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "RemoteOS.Server";
    public string Audience { get; set; } = "RemoteOS.Client";
    public TimeSpan AccessTokenTtl { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenTtl { get; set; } = TimeSpan.FromDays(7);

    /// <summary>appsettings.json 里的默认占位密钥，生产环境必须替换。</summary>
    public const string DefaultInsecureSecret = "REPLACE_IN_PRODUCTION_at_least_32_chars_long_random__";
}
