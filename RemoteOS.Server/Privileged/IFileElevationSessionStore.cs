using System.Security.Claims;

namespace Server.Privileged;

public interface IFileElevationSessionStore
{
    bool IsElevated(ClaimsPrincipal principal, string path);
    DateTimeOffset Grant(ClaimsPrincipal principal, string path);
}
