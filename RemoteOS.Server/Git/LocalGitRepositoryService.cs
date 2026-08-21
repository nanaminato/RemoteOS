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
        // tformat 会在每条 commit 后自动追加换行（不含最后一条尾部多余空行）；字段间用 \x01(SOH) 分隔，避免与 subject/body 中的制表符/空格冲突
        var format = "%H%x01%h%x01%an%x01%ae%x01%aI%x01%s%x01%b";
        var result = await RunGitAsync(gitPath, repo.Path,
            ["log", $"--pretty=tformat:{format}", "--date=iso-strict", $"-n {limit}", $"--skip={skip}"],
            cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git log failed: {result.Error}");

        var commits = new List<GitCommitDto>();
        // 先按行拆分每条 commit，再对单条 commit 按 SOH(\x01) 拆 7 个字段 — body 为空也不会丢失字段占位
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            // 限制最多拆 7 段，第 7 段(body)可包含后续的分隔符字符
            var parts = line.Split('\x01', 7, StringSplitOptions.None);
            if (parts.Length < 6) continue;
            var sha = parts[0].Trim();
            var shortSha = parts[1].Trim();
            var author = parts[2];
            var email = parts[3];
            var date = parts[4];
            var subject = parts[5];
            var body = parts.Length > 6 ? parts[6] : null;
            if (string.IsNullOrEmpty(body)) body = null;
            if (string.IsNullOrEmpty(sha)) continue;
            commits.Add(new GitCommitDto(sha, shortSha, author, email, date, subject, body));
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

    // ── 路径探测与初始化 ──

    public async Task<GitRepositoryProbeDto> ProbeRepositoryAsync(string path, Guid userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Repository path must not be empty.", nameof(path));
        if (!Path.IsPathRooted(path))
            throw new ArgumentException("Repository path must be absolute.", nameof(path));
        if (!Directory.Exists(path))
            throw new ArgumentException("Repository path does not exist.", nameof(path));

        var gitPath = gitCli.ResolveGitPath()
            ?? throw new InvalidOperationException("Git executable not found on the host.");

        // 是否是 git 仓库
        var revParse = await RunGitAsync(gitPath, path, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (!revParse.Success || !revParse.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return new GitRepositoryProbeDto(false);

        // 是否有提交
        var headSha = await RunGitAsync(gitPath, path, ["rev-parse", "HEAD"], cancellationToken);
        var hasCommits = headSha.Success && !string.IsNullOrWhiteSpace(headSha.Output.Trim());

        string? currentBranch = null;
        string? defaultBranch = null;
        if (hasCommits)
        {
            var branchResult = await RunGitAsync(gitPath, path, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            if (branchResult.Success)
            {
                var name = branchResult.Output.Trim();
                currentBranch = string.IsNullOrEmpty(name) || name == "HEAD" ? null : name;
            }
        }

        var defaultBranchResult = await RunGitAsync(gitPath, path,
            ["config", "--get", "init.defaultBranch"], cancellationToken);
        if (defaultBranchResult.Success)
        {
            var db = defaultBranchResult.Output.Trim();
            defaultBranch = string.IsNullOrEmpty(db) ? null : db;
        }
        defaultBranch ??= "main";

        return new GitRepositoryProbeDto(true, hasCommits, currentBranch, defaultBranch,
            await GetRemotesAsync(gitPath, path, cancellationToken));
    }

    public async Task<GitOperationResult> InitRepositoryAsync(string path, Guid userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Repository path must not be empty.", nameof(path));
        if (!Path.IsPathRooted(path))
            throw new ArgumentException("Repository path must be absolute.", nameof(path));
        if (!Directory.Exists(path))
            throw new ArgumentException("Repository path does not exist.", nameof(path));

        var gitPath = gitCli.ResolveGitPath()
            ?? throw new InvalidOperationException("Git executable not found on the host.");

        var initResult = await RunGitAsync(gitPath, path, ["init"], cancellationToken);
        if (!initResult.Success)
            return new GitOperationResult(false, "init", Message: initResult.Error);
        // git init 输出形如 "Initialized empty Git repository in /path/.git/"；不解析也行。
        return new GitOperationResult(true, "init");
    }

    // ── 远程仓库（remote）管理 ──

    public async Task<IReadOnlyList<GitRemoteDto>> ListRemotesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await GetRemotesAsync(gitPath, repo.Path, cancellationToken);
    }

    public async Task<GitOperationResult> AddRemoteAsync(Guid id, Guid userId, GitRemoteRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Url))
            return new GitOperationResult(false, "add-remote", Message: "Remote name and URL are required.");
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["remote", "add", request.Name, request.Url], cancellationToken);
            if (!result.Success)
                return new GitOperationResult(false, "add-remote", Message: result.Error);
            if (!string.IsNullOrEmpty(request.PushUrl))
            {
                var pushResult = await RunGitAsync(gitPath, repo.Path,
                    ["remote", "set-url", "--push", request.Name, request.PushUrl], cancellationToken);
                if (!pushResult.Success)
                    return new GitOperationResult(false, "add-remote", Message: $"Remote added but push URL update failed: {pushResult.Error}");
            }
            return new GitOperationResult(true, "add-remote");
        });
    }

    public async Task<GitOperationResult> UpdateRemoteAsync(Guid id, Guid userId, string name, GitRemoteRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(request.Url))
            return new GitOperationResult(false, "update-remote", Message: "Remote name and URL are required.");
        return await WithWriteLockAsync(id, async () =>
        {
            // 若新旧名不同，先重命名；同名则只是 set-url
            if (!string.Equals(name, request.Name, StringComparison.Ordinal))
            {
                var rename = await RunGitAsync(gitPath, repo.Path, ["remote", "rename", name, request.Name], cancellationToken);
                if (!rename.Success)
                    return new GitOperationResult(false, "update-remote", Message: rename.Error);
                name = request.Name;
            }
            var setUrl = await RunGitAsync(gitPath, repo.Path, ["remote", "set-url", name, request.Url], cancellationToken);
            if (!setUrl.Success)
                return new GitOperationResult(false, "update-remote", Message: setUrl.Error);
            if (!string.IsNullOrEmpty(request.PushUrl))
            {
                var setPush = await RunGitAsync(gitPath, repo.Path,
                    ["remote", "set-url", "--push", name, request.PushUrl], cancellationToken);
                if (!setPush.Success)
                    return new GitOperationResult(false, "update-remote", Message: $"Fetch URL updated but push URL update failed: {setPush.Error}");
            }
            return new GitOperationResult(true, "update-remote");
        });
    }

    public async Task<GitOperationResult> RemoveRemoteAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        if (string.IsNullOrWhiteSpace(name))
            return new GitOperationResult(false, "remove-remote", Message: "Remote name is required.");
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["remote", "remove", name], cancellationToken);
            return new GitOperationResult(result.Success, "remove-remote", Message: result.Success ? null : result.Error);
        });
    }

    private async Task<IReadOnlyList<GitRemoteDto>> GetRemotesAsync(string gitPath, string repoPath, CancellationToken cancellationToken)
    {
        // 注意：--no-color 是 git 全局选项，必须放在子命令之前；而 git 在 stdout 重定向
        // 时会自动禁用彩色输出，所以这里直接省略即可，避免子命令不认该参数导致命令失败。
        var result = await RunGitAsync(gitPath, repoPath, ["remote", "-v"], cancellationToken);
        if (!result.Success)
            return Array.Empty<GitRemoteDto>();

        // git remote -v 输出形如:
        //   origin  https://example.com/repo.git (fetch)
        //   origin  git@example.com:repo.git (push)
        //   upstream        https://example.com/up.git (fetch)
        //   upstream        https://example.com/up.git (push)
        // 名字与 URL 之间用制表符或多个空格分隔；行尾的 (fetch)/(push) 表示 URL 类型
        var remotes = new Dictionary<string, (string? Fetch, string? Push)>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            // 行尾标记
            const string fetchTag = "(fetch)";
            const string pushTag = "(push)";
            bool isPush = trimmed.EndsWith(pushTag, StringComparison.Ordinal);
            bool isFetch = trimmed.EndsWith(fetchTag, StringComparison.Ordinal);
            if (!isPush && !isFetch) continue;
            var body = trimmed[..^(isPush ? pushTag.Length : fetchTag.Length)].Trim();
            // 取第一个空白分隔
            var sep = -1;
            for (var i = 0; i < body.Length; i++)
            {
                if (char.IsWhiteSpace(body[i])) { sep = i; break; }
            }
            if (sep <= 0) continue;
            var remoteName = body[..sep].Trim();
            var url = body[sep..].Trim();
            if (string.IsNullOrEmpty(remoteName) || string.IsNullOrEmpty(url)) continue;

            if (!remotes.TryGetValue(remoteName, out var entry))
                entry = (null, null);
            if (isPush) entry = (entry.Fetch, url);
            else entry = (url, entry.Push);
            remotes[remoteName] = entry;
        }

        return remotes
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new GitRemoteDto(kv.Key, kv.Value.Fetch ?? string.Empty, kv.Value.Push))
            .ToArray();
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

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("# branch.head"))
            {
                branch = line["# branch.head".Length..].Trim();
                if (branch == "(detached)") { isDetached = true; branch = "HEAD (detached)"; }
            }
            else if (line.StartsWith("# branch.upstream"))
            {
                upstream = line["# branch.upstream".Length..].Trim();
                if (string.IsNullOrEmpty(upstream)) upstream = null;
            }
            else if (line.StartsWith("# branch.ab"))
            {
                var abPart = line["# branch.ab".Length..].Trim();
                var abParts = abPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (abParts.Length >= 2)
                {
                    ahead = int.TryParse(abParts[0].TrimStart('+'), out var a) ? a : 0;
                    behind = int.TryParse(abParts[1].TrimStart('-'), out var b) ? b : 0;
                }
            }
            else if (line.StartsWith("u "))
            {
                // Unmerged entry: "u XY sub mH mI mW hH hI path" — path 仍为最后一段
                var (path, _) = TakePathAfterNSpaces(line, 10);
                if (!string.IsNullOrEmpty(path))
                    conflicts.Add(new GitFileChangeDto(path, Staged: false, Status: "conflicted"));
            }
            else if (line.StartsWith("1 ") || line.StartsWith("2 "))
            {
                // Type 1: "1 XY sub mH mI mW hH hI path" — 8 个空格字段 + 路径
                // Type 2: "2 XY sub mH mI mW hH hI score path⇥orig_path" — 9 个空格字段 + 路径(可能 TAB+原始路径)
                var isRename = line[0] == '2';
                var (path, origPath) = TakePathAfterNSpaces(line, isRename ? 9 : 8);
                if (string.IsNullOrEmpty(path)) continue;

                var xyText = line.Length > 3 ? line[2..4] : "  ";
                var x = xyText[0]; // staged side
                var y = xyText[1]; // unstaged side

                var status = MapStatusChar(y != '.' && y != ' ' ? y : x);

                // X = staged status: ' ' 无、'.' 无 之外的字母表示 staged 改动
                if (x != ' ' && x != '.' && x != '?')
                    staged.Add(new GitFileChangeDto(path, Staged: true, Status: MapStatusChar(x), OldPath: origPath));
                // Y = unstaged status: ' ' 无、'.' 无
                if (y != ' ' && y != '.' && y != '?')
                    unstaged.Add(new GitFileChangeDto(path, Staged: false, Status: MapStatusChar(y), OldPath: origPath));
            }
            else if (line.StartsWith("? "))
            {
                var filePath = line[2..].Trim();
                if (!string.IsNullOrEmpty(filePath))
                    untracked.Add(new GitFileChangeDto(filePath, Staged: false, Status: "untracked"));
            }
        }
        return new GitStatusDto(branch, staged, unstaged, untracked, conflicts, upstream, ahead, behind, isDetached);
    }

    /// <summary>从 porcelain v2 状态行取最后一段路径（可能含空格）：跳过前 n 个空格分隔的字段，剩余即路径。
    /// Type 2（rename/copy）里路径字段格式为 path⇥orig_path，用 TAB 分隔；返回 (path, origPath_or_null)。</summary>
    private static (string Path, string? OrigPath) TakePathAfterNSpaces(string line, int spaceFieldCount)
    {
        var remaining = line.AsSpan();
        for (int i = 0; i < spaceFieldCount; i++)
        {
            // 跳过一个字段：直到空格
            var sp = remaining.IndexOf(' ');
            if (sp < 0) return ("", null); // 字段不足
            remaining = remaining[(sp + 1)..];
            // 跳过连续空格（通常只有一个）
            while (remaining.Length > 0 && remaining[0] == ' ') remaining = remaining[1..];
        }

        var tail = remaining.ToString();
        // type 2 的 rename/copy 行：路径部分是 path⇥orig_path（TAB 分隔）
        var tab = tail.IndexOf('\t');
        if (tab >= 0)
        {
            var path = tail[..tab].Trim();
            var orig = tail[(tab + 1)..].Trim();
            return (path, string.IsNullOrEmpty(orig) ? null : orig);
        }
        return (tail.Trim(), null);
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
