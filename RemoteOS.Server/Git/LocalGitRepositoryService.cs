using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RemoteOS.Protocol.Git;
using Server.Domain;
using Server.Storage.Sqlite;

namespace Server.Git;

/// <summary>Singleton service that invokes the host git CLI and manages repository registrations.
/// Write operations are serialized per-repository via SemaphoreSlim to avoid index.lock conflicts.
/// Runtime state (status/branches/log/diff) is never persisted—only GitRepository registration records.</summary>
public sealed class LocalGitRepositoryService(IDbContextFactory<RemoteOsDbContext> dbFactory, IHostGitCli gitCli, ILogger<LocalGitRepositoryService> logger) : IGitRepositoryService
{
    private const int MaxDiffPatchSize = 200 * 1024; // 200KB
    private static readonly TimeSpan SemaphoreTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _writeLocks = new();

    // ── Host Git engine probe & install ──

    public async Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken = default)
    {
        var path = gitCli.ResolveGitPath();
        if (string.IsNullOrEmpty(path))
            return new GitEngineStatusDto(false, ProblemCode: "not_installed", CanAutoInstall: CanAutoInstallGit());
        var version = await GetGitVersionAsync(path, cancellationToken);
        return new GitEngineStatusDto(true, ProblemCode: "", Version: version, ExecutablePath: path);
    }

    public async Task<GitEngineInstallResult> InstallEngineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var winget = ResolveWingetPath();
                if (string.IsNullOrEmpty(winget))
                    return new GitEngineInstallResult(false, "Windows 包管理器 winget 不可用，请手动安装 Git for Windows：https://git-scm.com/download/win");
                var args = new[]
                {
                    "install", "--id", "Git.Git", "-e", "--source", "winget",
                    "--silent", "--accept-package-agreements", "--accept-source-agreements",
                };
                var result = await RunProcessAsync(winget, Environment.CurrentDirectory, args, cancellationToken);
                if (!result.Success && gitCli.ResolveGitPath() is null)
                    return new GitEngineInstallResult(false, string.IsNullOrWhiteSpace(result.Error) ? $"winget 安装失败，退出码 {result.ExitCode}" : result.Error.Trim());
            }
            else if (OperatingSystem.IsLinux())
            {
                if (File.Exists("/usr/bin/apt-get"))
                {
                    await RunProcessAsync("/usr/bin/apt-get", "/", ["update", "-y"], cancellationToken);
                    var r = await RunProcessAsync("/usr/bin/apt-get", "/", ["install", "-y", "git"], cancellationToken);
                    if (!r.Success && gitCli.ResolveGitPath() is null)
                        return new GitEngineInstallResult(false, string.IsNullOrWhiteSpace(r.Error) ? $"apt-get install git 失败，退出码 {r.ExitCode}" : r.Error.Trim());
                }
                else if (File.Exists("/usr/bin/dnf"))
                {
                    var r = await RunProcessAsync("/usr/bin/dnf", "/", ["install", "-y", "git"], cancellationToken);
                    if (!r.Success && gitCli.ResolveGitPath() is null)
                        return new GitEngineInstallResult(false, string.IsNullOrWhiteSpace(r.Error) ? $"dnf install git 失败，退出码 {r.ExitCode}" : r.Error.Trim());
                }
                else if (File.Exists("/usr/bin/yum"))
                {
                    var r = await RunProcessAsync("/usr/bin/yum", "/", ["install", "-y", "git"], cancellationToken);
                    if (!r.Success && gitCli.ResolveGitPath() is null)
                        return new GitEngineInstallResult(false, string.IsNullOrWhiteSpace(r.Error) ? $"yum install git 失败，退出码 {r.ExitCode}" : r.Error.Trim());
                }
                else if (File.Exists("/usr/bin/pacman"))
                {
                    var r = await RunProcessAsync("/usr/bin/pacman", "/", ["-S", "--noconfirm", "git"], cancellationToken);
                    if (!r.Success && gitCli.ResolveGitPath() is null)
                        return new GitEngineInstallResult(false, string.IsNullOrWhiteSpace(r.Error) ? $"pacman -S git 失败，退出码 {r.ExitCode}" : r.Error.Trim());
                }
                else
                {
                    return new GitEngineInstallResult(false, "未识别的 Linux 包管理器（apt-get/dnf/yum/pacman 都不可用），请手动安装 git。");
                }
            }
            else
            {
                return new GitEngineInstallResult(false, "当前操作系统不支持自动安装 Git。");
            }

            return gitCli.ResolveGitPath() is not null
                ? new GitEngineInstallResult(true, "Git 安装成功。")
                : new GitEngineInstallResult(false, "安装命令执行成功，但仍未检测到 git 可执行文件，请检查 PATH 配置或重启服务。");
        }
        catch (Exception ex)
        {
            return new GitEngineInstallResult(false, $"安装过程中出错：{ex.Message}");
        }
    }

    private async Task<string?> GetGitVersionAsync(string gitPath, CancellationToken cancellationToken)
    {
        var r = await RunGitAsync(gitPath, Environment.CurrentDirectory, ["--version"], cancellationToken);
        if (!r.Success) return null;
        var line = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line)) return null;
        const string prefix = "git version";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line.Substring(prefix.Length).Trim()
            : line;
    }

    private static bool CanAutoInstallGit()
    {
        if (OperatingSystem.IsWindows()) return ResolveWingetPath() is not null;
        if (OperatingSystem.IsLinux())
            return File.Exists("/usr/bin/apt-get") || File.Exists("/usr/bin/dnf")
                || File.Exists("/usr/bin/yum") || File.Exists("/usr/bin/pacman");
        return false;
    }

    private static string? ResolveWingetPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps", "winget.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("where", ["winget.exe"])
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                }
            };
            p.Start();
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(TimeSpan.FromSeconds(3));
            if (p.ExitCode == 0)
            {
                var first = o.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first) && File.Exists(first)) return first;
            }
        }
        catch { /* ignored */ }
        return null;
    }

    private static async Task<(int ExitCode, string Output, string Error, bool Success)> RunProcessAsync(
        string exe, string workingDir, string[] args, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputTask, errorTask);
        return (process.ExitCode, await outputTask, await errorTask, process.ExitCode == 0);
    }

    // ── Repository registration ──

    public async Task<IReadOnlyList<GitRepositoryDto>> ListRepositoriesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repos = await db.Set<GitRepository>()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
        return repos.Count > 0 ? repos.Select(r => r.ToDto()).ToArray() : Array.Empty<GitRepositoryDto>();
    }

    public async Task<GitRepositoryDto?> GetRepositoryAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await db.Set<GitRepository>().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);
        if (repo is null) return null;
        var (branch, upstream, ahead, behind, isDetached, uncommitted) = await GetBranchSummaryAsync(repo.Path, cancellationToken);
        return repo.ToDto(branch, null, ahead, behind, !string.IsNullOrEmpty(upstream), uncommitted);
    }

    public async Task<GitRepositoryDto> RegisterRepositoryAsync(GitRepositoryRegistration registration, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(registration.Path))
            throw new ArgumentException("Repository path must be absolute.", nameof(registration.Path));
        if (!Directory.Exists(registration.Path))
            throw new ArgumentException("Repository path does not exist.", nameof(registration.Path));

        var gitPath = ResolveGitPathOrThrow();

        var revParse = await RunGitAsync(gitPath, registration.Path, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (!revParse.Success || !revParse.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The path is not a Git repository.", nameof(registration.Path));

        var repo = new GitRepository
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = registration.Name,
            Path = registration.Path,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Set<GitRepository>().Add(repo);
        await db.SaveChangesAsync(cancellationToken);
        return repo.ToDto();
    }

    public async Task<bool> UnregisterRepositoryAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await db.Set<GitRepository>().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);
        if (repo is null) return false;
        db.Set<GitRepository>().Remove(repo);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Real-time git operations ──

    public async Task<GitStatusDto> GetStatusAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        var result = await RunGitAsync(gitPath, repo.Path, ["status", "--porcelain=v2", "--branch"], cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git status failed: {result.Error}");
        return ParseStatus(result.Output);
    }

    public async Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        var fmt = "%(refname:short)%09%(upstream:short)%09%(upstream:track)%09%(HEAD)";
        var result = await RunGitAsync(gitPath, repo.Path,
            ["for-each-ref", $"--format={fmt}", "refs/heads", "refs/remotes"], cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git for-each-ref failed: {result.Error}");

        var branches = new List<GitBranchDto>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4) continue;
            var name = parts[0];
            var upstream = parts[1].Length > 0 ? parts[1] : null;
            var track = parts[2];
            var isHead = parts[3] == "*";
            var isRemote = name.Contains('/');
            var (ahead, behind) = ParseTrack(track);
            branches.Add(new GitBranchDto(name, isRemote, isHead, false, upstream, ahead, behind));
        }
        return branches;
    }

    public async Task<GitOperationResult> CreateBranchAsync(Guid id, Guid userId, GitBranchCreateRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "branch", request.Name };
            if (request.Track) args.Add("--track");
            if (!string.IsNullOrEmpty(request.StartPoint)) args.Add(request.StartPoint);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            return new GitOperationResult(result.Success, "create-branch", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> DeleteBranchAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var branchResult = await RunGitAsync(gitPath, repo.Path, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            if (branchResult.Success && branchResult.Output.Trim() == name)
                return new GitOperationResult(false, "delete-branch", Message: "Cannot delete the current branch.");
            var result = await RunGitAsync(gitPath, repo.Path, ["branch", "-d", name], cancellationToken);
            return new GitOperationResult(result.Success, "delete-branch", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> CheckoutAsync(Guid id, Guid userId, GitCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "checkout" };
            if (request.CreateIfMissing) args.Add("-b");
            args.Add(request.Branch);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            var conflicts = result.Success ? null : await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(result.Success, "checkout", Conflicts: conflicts, Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> CommitAsync(Guid id, Guid userId, GitCommitRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            if (request.Paths.Count > 0)
            {
                foreach (var path in request.Paths)
                    if (!IsPathSafe(repo.Path, path))
                        return new GitOperationResult(false, "commit", Message: $"Path outside repository: {path}");
                var addArgs = new List<string> { "add", "--" };
                addArgs.AddRange(request.Paths);
                var addResult = await RunGitAsync(gitPath, repo.Path, [.. addArgs], cancellationToken);
                if (!addResult.Success)
                    return new GitOperationResult(false, "commit", Message: addResult.Error);
            }
            var args = new List<string> { "commit", "-m", request.Message };
            if (request.Amend) args.Add("--amend");
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            return new GitOperationResult(result.Success, "commit", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> FetchAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["fetch"], cancellationToken);
            return new GitOperationResult(result.Success, "fetch",
                RequiresCredentials: !result.Success && IsCredentialError(result.Error),
                Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> PullAsync(Guid id, Guid userId, GitPullRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "pull" };
            if (string.Equals(request.Strategy, "rebase", StringComparison.OrdinalIgnoreCase))
                args.Add("--rebase");
            if (!string.IsNullOrEmpty(request.Remote)) args.Add(request.Remote);
            if (!string.IsNullOrEmpty(request.Refspec)) args.Add(request.Refspec);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(result.Success || conflicts is not null, "pull",
                Conflicts: conflicts,
                RequiresCredentials: !result.Success && IsCredentialError(result.Error),
                Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> PushAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["push"], cancellationToken);
            return new GitOperationResult(result.Success, "push",
                RequiresCredentials: !result.Success && IsCredentialError(result.Error),
                Message: result.Success ? null : result.Error);
        });
    }

    public async Task<IReadOnlyList<GitCommitDto>> GetLogAsync(Guid id, Guid userId, int limit = 200, int skip = 0, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        var format = "%H%x00%h%x00%an%x00%ae%x00%aI%x00%s%x00%b%x00";
        var result = await RunGitAsync(gitPath, repo.Path,
            ["log", $"--pretty=format:{format}", "--date=iso-strict", $"-n {limit}", $"--skip={skip}"],
            cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git log failed: {result.Error}");

        var commits = new List<GitCommitDto>();
        foreach (var entry in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry.Split('\n', 2);
            var parts = fields[0].Split('\t');
            if (parts.Length < 6) continue;
            var body = fields.Length > 1 ? fields[1].TrimEnd('\0') : null;
            commits.Add(new GitCommitDto(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], body));
        }
        return commits;
    }

    public async Task<GitDiffDto> GetDiffAsync(Guid id, Guid userId, string path, bool staged = false, string? @ref = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        if (!IsPathSafe(repo.Path, path))
            throw new ArgumentException("Path outside repository.", nameof(path));

        var args = new List<string> { "diff", "--no-color" };
        if (staged) args.Add("--cached");
        if (!string.IsNullOrEmpty(@ref)) args.Add(@ref);
        args.Add("--");
        args.Add(path);

        var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
        var patch = result.Output;
        var truncated = false;
        if (patch.Length > MaxDiffPatchSize)
        {
            var statArgs = new List<string> { "diff", "--stat" };
            if (staged) statArgs.Add("--cached");
            if (!string.IsNullOrEmpty(@ref)) statArgs.Add(@ref);
            statArgs.Add("--");
            statArgs.Add(path);
            var statResult = await RunGitAsync(gitPath, repo.Path, [.. statArgs], cancellationToken);
            patch = statResult.Output;
            truncated = true;
        }

        var numstatArgs = new List<string> { "diff", "--numstat" };
        if (staged) numstatArgs.Add("--cached");
        if (!string.IsNullOrEmpty(@ref)) numstatArgs.Add(@ref);
        numstatArgs.Add("--");
        numstatArgs.Add(path);
        var numstat = await RunGitAsync(gitPath, repo.Path, [.. numstatArgs], cancellationToken);
        var (additions, deletions) = ParseNumstat(numstat.Output);

        var isBinary = patch.Contains("Binary files", StringComparison.OrdinalIgnoreCase) ||
                       patch.Contains("GIT binary patch", StringComparison.OrdinalIgnoreCase);

        return new GitDiffDto(path, null, isBinary ? "" : patch, additions, deletions, isBinary, truncated);
    }

    public async Task<GitOperationResult> RevertAsync(Guid id, Guid userId, GitRevertRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "revert" };
            if (request.NoCommit) args.Add("--no-commit");
            args.Add(request.Sha);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(result.Success || conflicts is not null, "revert",
                Conflicts: conflicts, Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> ResolveConflictsAsync(Guid id, Guid userId, GitResolveRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            foreach (var path in request.Paths)
                if (!IsPathSafe(repo.Path, path))
                    return new GitOperationResult(false, "resolve", Message: $"Path outside repository: {path}");

            if (request.Paths.Count > 0)
            {
                var addArgs = new List<string> { "add", "--" };
                addArgs.AddRange(request.Paths);
                var addResult = await RunGitAsync(gitPath, repo.Path, [.. addArgs], cancellationToken);
                if (!addResult.Success)
                    return new GitOperationResult(false, "resolve", Message: addResult.Error);
            }

            if (request.ContinueMerge)
            {
                var mergeHeadExists = File.Exists(Path.Combine(repo.Path, ".git", "MERGE_HEAD"));
                if (mergeHeadExists)
                {
                    var contResult = await RunGitAsync(gitPath, repo.Path, ["merge", "--continue"], cancellationToken);
                    var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
                    return new GitOperationResult(contResult.Success, "resolve",
                        Conflicts: conflicts, Message: contResult.Success ? null : contResult.Error);
                }
                var rebaseResult = await RunGitAsync(gitPath, repo.Path, ["rebase", "--continue"], cancellationToken);
                var remainingConflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
                return new GitOperationResult(rebaseResult.Success, "resolve",
                    Conflicts: remainingConflicts, Message: rebaseResult.Success ? null : rebaseResult.Error);
            }
            return new GitOperationResult(true, "resolve");
        });
    }

    // ── Helpers ──

    private string ResolveGitPath() => gitCli.ResolveGitPath() ?? "";
    private string ResolveGitPathOrThrow() => gitCli.ResolveGitPath()
        ?? throw new InvalidOperationException("Git executable not found on the host.");

    private static async Task<GitRepository> GetRepoOrThrowAsync(RemoteOsDbContext db, Guid id, Guid userId, CancellationToken cancellationToken)
    {
        return await db.Set<GitRepository>().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException($"Repository {id} not found.");
    }

    private async Task<(string branch, string? upstream, int ahead, int behind, bool isDetached, int uncommitted)> GetBranchSummaryAsync(string repoPath, CancellationToken cancellationToken)
    {
        var gitPath = ResolveGitPath();
        if (string.IsNullOrEmpty(gitPath)) return ("unknown", null, 0, 0, false, 0);
        var result = await RunGitAsync(gitPath, repoPath, ["status", "--porcelain=v2", "--branch"], cancellationToken);
        if (!result.Success) return ("unknown", null, 0, 0, false, 0);
        var status = ParseStatus(result.Output);
        var uncommitted = status.Staged.Count + status.Unstaged.Count + status.Untracked.Count + status.Conflicts.Count;
        return (status.Branch, status.Upstream, status.Ahead, status.Behind, status.IsDetached, uncommitted);
    }

    private static GitStatusDto ParseStatus(string output)
    {
        string branch = "unknown";
        string? upstream = null;
        int ahead = 0, behind = 0;
        bool isDetached = false;
        var staged = new List<GitFileChangeDto>();
        var unstaged = new List<GitFileChangeDto>();
        var untracked = new List<GitFileChangeDto>();
        var conflicts = new List<GitFileChangeDto>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.head"))
            {
                branch = line.Substring("# branch.head".Length).Trim();
                if (branch == "(detached)") { isDetached = true; branch = "HEAD (detached)"; }
            }
            else if (line.StartsWith("# branch.upstream"))
            {
                upstream = line.Substring("# branch.upstream".Length).Trim();
                if (string.IsNullOrEmpty(upstream)) upstream = null;
            }
            else if (line.StartsWith("# branch.ab"))
            {
                var abParts = line.Substring("# branch.ab".Length).Trim().Split(' ');
                if (abParts.Length >= 2)
                {
                    ahead = int.TryParse(abParts[0].TrimStart('+'), out var a) ? a : 0;
                    behind = int.TryParse(abParts[1].TrimStart('-'), out var b) ? b : 0;
                }
            }
            else if (line.StartsWith("u "))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 11)
                    conflicts.Add(new GitFileChangeDto(parts[10], Staged: false, Status: "conflicted"));
            }
            else if (line.StartsWith("1 ") || line.StartsWith("2 "))
            {
                var parts = line.Split(' ');
                var xy = parts.Length > 1 ? parts[1] : "  ";
                var pathIndex = line.StartsWith("1 ") ? 8 : 9;
                var filePath = parts.Length > pathIndex ? parts[pathIndex] : "";
                var x = xy.Length > 0 ? xy[0] : ' ';
                var y = xy.Length > 1 ? xy[1] : ' ';
                var status = MapStatusChar(y != ' ' ? y : x);
                if (x != ' ' && x != '?')
                    staged.Add(new GitFileChangeDto(filePath, Staged: true, Status: status));
                if (y != ' ' && y != '?')
                    unstaged.Add(new GitFileChangeDto(filePath, Staged: false, Status: status));
            }
            else if (line.StartsWith("? "))
            {
                var parts = line.Split(' ', 2);
                if (parts.Length > 1)
                    untracked.Add(new GitFileChangeDto(parts[1].Trim(), Staged: false, Status: "untracked"));
            }
        }
        return new GitStatusDto(branch, staged, unstaged, untracked, conflicts, upstream, ahead, behind, isDetached);
    }

    private static string MapStatusChar(char c) => c switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        _ => "modified"
    };

    private static (int ahead, int behind) ParseTrack(string track)
    {
        if (string.IsNullOrWhiteSpace(track)) return (0, 0);
        int ahead = 0, behind = 0;
        var aheadMatch = System.Text.RegularExpressions.Regex.Match(track, @"ahead (\d+)");
        var behindMatch = System.Text.RegularExpressions.Regex.Match(track, @"behind (\d+)");
        if (aheadMatch.Success) ahead = int.Parse(aheadMatch.Groups[1].Value);
        if (behindMatch.Success) behind = int.Parse(behindMatch.Groups[1].Value);
        return (ahead, behind);
    }

    private static (int additions, int deletions) ParseNumstat(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return (0, 0);
        var parts = output.Trim().Split('\t');
        if (parts.Length < 2) return (0, 0);
        int additions = int.TryParse(parts[0], out var a) ? a : 0;
        int deletions = int.TryParse(parts[1], out var d) ? d : 0;
        return (additions, deletions);
    }

    private static bool IsPathSafe(string repoRoot, string path)
    {
        if (!Path.IsPathRooted(path))
            path = Path.Combine(repoRoot, path);
        var fullRepoRoot = Path.GetFullPath(repoRoot);
        var fullTarget = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRepoRoot, fullTarget);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool IsCredentialError(string error) =>
        error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("fatal: could not read", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<string>?> TryGetConflictPathsAsync(string gitPath, string repoPath, CancellationToken cancellationToken)
    {
        var statusResult = await RunGitAsync(gitPath, repoPath, ["status", "--porcelain=v2"], cancellationToken);
        if (!statusResult.Success) return null;
        var conflicts = new List<string>();
        foreach (var line in statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("u "))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 11)
                    conflicts.Add(parts[10]);
            }
        }
        return conflicts.Count > 0 ? conflicts : null;
    }

    private async Task<GitOperationResult> WithWriteLockAsync(Guid repoId, Func<Task<GitOperationResult>> operation)
    {
        var semaphore = _writeLocks.GetOrAdd(repoId, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(SemaphoreTimeout))
            return new GitOperationResult(false, "busy", Message: "Repository is busy, another operation is in progress.");
        try { return await operation(); }
        finally { semaphore.Release(); }
    }

    private async Task<CommandResult> RunGitAsync(string gitPath, string workingDir, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(gitPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir
                }
            };
            foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start())
                return new CommandResult(false, "", "start_failed");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return new CommandResult(process.ExitCode == 0, await outputTask, await errorTask);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return new CommandResult(false, "", "timeout");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new CommandResult(false, "", "git_not_found");
        }
    }

    private sealed record CommandResult(bool Success, string Output, string Error);
}
