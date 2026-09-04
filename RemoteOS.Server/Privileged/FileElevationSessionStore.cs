using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Server.Privileged;

/// <summary>Short-lived in-memory file elevation grants, scoped to one JWT and canonical path.</summary>
public sealed class FileElevationSessionStore : IFileElevationSessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _grants = new(StringComparer.Ordinal);

    public bool IsElevated(ClaimsPrincipal principal, string path)
    {
        if (string.Equals(principal.FindFirstValue(JwtRegisteredClaimNames.Name), "root", StringComparison.Ordinal)) return true;
        var key = Key(principal, path);
        return key is not null && _grants.TryGetValue(key, out var expires) && expires > DateTimeOffset.UtcNow;
    }

    public DateTimeOffset Grant(ClaimsPrincipal principal, string path)
    {
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        var key = Key(principal, path) ?? throw new InvalidOperationException("The access token has no id.");
        _grants[key] = expires;
        return expires;
    }

    private static string? Key(ClaimsPrincipal principal, string path)
    {
        var tokenId = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (string.IsNullOrWhiteSpace(tokenId)) return null;
        return tokenId + "\n" + Path.GetFullPath(path);
    }
}
