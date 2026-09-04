using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Server.Privileged;

/// <summary>Short-lived in-memory file elevation grants, scoped to one JWT and canonical path.</summary>
public sealed class FileElevationSessionStore : IFileElevationSessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, ElevationGrant> _grants = new(StringComparer.Ordinal);

    public bool IsElevated(ClaimsPrincipal principal, string path)
    {
        if (string.Equals(principal.FindFirstValue(JwtRegisteredClaimNames.Name), "root", StringComparison.Ordinal)) return true;
        var tokenId = TokenId(principal);
        if (tokenId is null) return false;
        var canonicalPath = CanonicalPath(path);
        return _grants.TryGetValue(Key(tokenId, canonicalPath), out var exact) && exact.ExpiresAt > DateTimeOffset.UtcNow
            || _grants.Any(pair => pair.Value.IncludeDescendants
                && pair.Value.ExpiresAt > DateTimeOffset.UtcNow
                && IsDescendantKey(tokenId, canonicalPath, pair.Key));
    }

    public bool IsElevated(ClaimsPrincipal principal, params string[] paths)
        => paths.All(path => IsElevated(principal, path));

    public DateTimeOffset Grant(ClaimsPrincipal principal, string path, bool includeDescendants = false)
    {
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        var tokenId = TokenId(principal) ?? throw new InvalidOperationException("The access token has no id.");
        _grants[Key(tokenId, CanonicalPath(path))] = new ElevationGrant(expires, includeDescendants);
        return expires;
    }

    private static bool IsDescendantKey(string tokenId, string path, string grantKey)
    {
        var prefix = tokenId + "\n";
        if (!grantKey.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var directory = grantKey[prefix.Length..];
        return string.Equals(path, directory, PathComparison)
            || path.StartsWith(EnsureTrailingSeparator(directory), PathComparison);
    }

    private static string? TokenId(ClaimsPrincipal principal)
        => principal.FindFirstValue(JwtRegisteredClaimNames.Jti) is { Length: > 0 } tokenId ? tokenId : null;

    private static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, PathComparison)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string Key(string tokenId, string path) => tokenId + "\n" + path;
    private static string EnsureTrailingSeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
        ? path : path + Path.DirectorySeparatorChar;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record ElevationGrant(DateTimeOffset ExpiresAt, bool IncludeDescendants);
}
