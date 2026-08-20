using RemoteOS.Protocol.Git;

namespace Client.Apps.Git;

/// <summary>Typed JWT client for the server's Git repository facade.</summary>
public interface IRemoteGitClient
{
    Task<IReadOnlyList<GitRepositoryDto>> ListRepositoriesAsync(CancellationToken cancellationToken = default);
    Task<GitRepositoryDto?> GetRepositoryAsync(string id, CancellationToken cancellationToken = default);
    Task<GitRepositoryDto> RegisterRepositoryAsync(GitRepositoryRegistration registration, CancellationToken cancellationToken = default);
    Task<bool> UnregisterRepositoryAsync(string id, CancellationToken cancellationToken = default);
    Task<GitStatusDto> GetStatusAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(string id, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateBranchAsync(string id, GitBranchCreateRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> DeleteBranchAsync(string id, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CheckoutAsync(string id, GitCheckoutRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CommitAsync(string id, GitCommitRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> FetchAsync(string id, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PullAsync(string id, GitPullRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PushAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitCommitDto>> GetLogAsync(string id, int limit = 200, int skip = 0, CancellationToken cancellationToken = default);
    Task<GitDiffDto> GetDiffAsync(string id, string path, bool staged = false, string? @ref = null, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RevertAsync(string id, GitRevertRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResolveConflictsAsync(string id, GitResolveRequest request, CancellationToken cancellationToken = default);
}
