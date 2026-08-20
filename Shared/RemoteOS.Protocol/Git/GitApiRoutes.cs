using RemoteOS.Protocol.Common;

namespace RemoteOS.Protocol.Git;

/// <summary>Routes for the server-side Git repository integration. Shared by server endpoint registration and client URL composition.</summary>
public static class GitApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
    public const string Repositories = $"/{V1}/git/repositories";
    public const string RepositoryById = $"/{V1}/git/repositories/{{id}}";
    public const string Status = $"/{V1}/git/repositories/{{id}}/status";
    public const string Branches = $"/{V1}/git/repositories/{{id}}/branches";
    public const string BranchByName = $"/{V1}/git/repositories/{{id}}/branches/{{name}}";
    public const string Checkout = $"/{V1}/git/repositories/{{id}}/checkout";
    public const string Commit = $"/{V1}/git/repositories/{{id}}/commit";
    public const string Pull = $"/{V1}/git/repositories/{{id}}/pull";
    public const string Push = $"/{V1}/git/repositories/{{id}}/push";
    public const string Log = $"/{V1}/git/repositories/{{id}}/log";
    public const string Diff = $"/{V1}/git/repositories/{{id}}/diff";
    public const string Revert = $"/{V1}/git/repositories/{{id}}/revert";
    public const string Resolve = $"/{V1}/git/repositories/{{id}}/resolve";
    public const string Fetch = $"/{V1}/git/repositories/{{id}}/fetch";
}
