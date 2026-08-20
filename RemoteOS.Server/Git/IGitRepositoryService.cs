using RemoteOS.Protocol.Git;

namespace Server.Git;

/// <summary>The only server boundary allowed to invoke the host's git CLI and manage repository registrations.</summary>
public interface IGitRepositoryService
{
    // ── Host Git engine probe & install ──
    Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken = default);
    Task<GitEngineInstallResult> InstallEngineAsync(CancellationToken cancellationToken = default);

    // ── Repository registration (persisted in SQLite, isolated by user) ──
    Task<IReadOnlyList<GitRepositoryDto>> ListRepositoriesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<GitRepositoryDto?> GetRepositoryAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitRepositoryDto> RegisterRepositoryAsync(GitRepositoryRegistration registration, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> UnregisterRepositoryAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);

    // ── Real-time git operations (not persisted) ──
    Task<GitStatusDto> GetStatusAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateBranchAsync(Guid id, Guid userId, GitBranchCreateRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> DeleteBranchAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CheckoutAsync(Guid id, Guid userId, GitCheckoutRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CommitAsync(Guid id, Guid userId, GitCommitRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> FetchAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PullAsync(Guid id, Guid userId, GitPullRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PushAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitCommitDto>> GetLogAsync(Guid id, Guid userId, int limit = 200, int skip = 0, CancellationToken cancellationToken = default);
    Task<GitDiffDto> GetDiffAsync(Guid id, Guid userId, string path, bool staged = false, string? @ref = null, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RevertAsync(Guid id, Guid userId, GitRevertRequest request, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResolveConflictsAsync(Guid id, Guid userId, GitResolveRequest request, CancellationToken cancellationToken = default);
}
