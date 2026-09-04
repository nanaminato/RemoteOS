using System.Security.Claims;

namespace Server.Privileged;

public interface IFileElevationSessionStore
{
    bool IsElevated(ClaimsPrincipal principal, string path);
    bool IsElevated(ClaimsPrincipal principal, params string[] paths);
    DateTimeOffset Grant(ClaimsPrincipal principal, string path, bool includeDescendants = false);
}
