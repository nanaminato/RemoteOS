using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Workspace;
using Server.Domain;

namespace Server.Identity;

/// <summary>JWT 令牌签发服务。签发短期 AccessToken（REST/Hub 鉴权）与长期 RefreshToken（换新）。
/// AccessToken claims 携带 sub/name/workspace_id/device_id/role/jti，
/// RefreshToken 为随机 32 字节并登记到 AuthSessionStore。见 Protocol.md §7。</summary>
public sealed class JwtTokenService
{
    private readonly JwtOptions _opt;
    private readonly AuthSessionStore _sessions;

    public JwtTokenService(IOptions<JwtOptions> opt, AuthSessionStore sessions)
    {
        _opt = opt.Value;
        _sessions = sessions;
    }

    /// <summary>签发令牌对。role 决定 JWT 中的 role claim（首个登录设备为 Controller）。</summary>
    public AuthTokens Issue(User user, Workspace workspace, Device device, DeviceRole role)
    {
        var now = DateTimeOffset.UtcNow;
        var accessExp = now.Add(_opt.AccessTokenTtl);
        var refreshExp = now.Add(_opt.RefreshTokenTtl);

        var sessionId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, user.Username),
            new Claim("workspace_id", workspace.Id.ToString()),
            new Claim("device_id", device.Id.ToString()),
            new Claim("role", role.ToString().ToLowerInvariant()),
            new Claim(JwtRegisteredClaimNames.Jti, sessionId.ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: accessExp.UtcDateTime,
            signingCredentials: creds);
        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        // RefreshToken：随机 32 字节，登记到吊销簿（jti → user/workspace/device/exp）
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessions.Register(sessionId, refreshToken, user.Id, workspace.Id, device.Id, refreshExp);

        return new AuthTokens(accessToken, refreshToken, accessExp, refreshExp);
    }
}
