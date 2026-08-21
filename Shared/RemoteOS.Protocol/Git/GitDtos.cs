using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Git;

/// <summary>Registered repository metadata + summary (list item).</summary>
public sealed record GitRepositoryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("currentBranch")] string? CurrentBranch = null,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch = null,
    [property: JsonPropertyName("headAhead")] int HeadAhead = 0,
    [property: JsonPropertyName("headBehind")] int HeadBehind = 0,
    [property: JsonPropertyName("hasUpstream")] bool HasUpstream = false,
    [property: JsonPropertyName("uncommittedCount")] int UncommittedCount = 0);

/// <summary>Repository detail with branch/upstream info.</summary>
public sealed record GitRepositoryDetailDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("currentBranch")] string CurrentBranch,
    [property: JsonPropertyName("upstreamBranch")] string? UpstreamBranch = null,
    [property: JsonPropertyName("remoteUrl")] string? RemoteUrl = null,
    [property: JsonPropertyName("isDetached")] bool IsDetached = false,
    [property: JsonPropertyName("isClean")] bool IsClean = true,
    [property: JsonPropertyName("aheadCount")] int AheadCount = 0,
    [property: JsonPropertyName("behindCount")] int BehindCount = 0);

/// <summary>Working tree status aggregate.</summary>
public sealed record GitStatusDto(
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("staged")] IReadOnlyList<GitFileChangeDto> Staged,
    [property: JsonPropertyName("unstaged")] IReadOnlyList<GitFileChangeDto> Unstaged,
    [property: JsonPropertyName("untracked")] IReadOnlyList<GitFileChangeDto> Untracked,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<GitFileChangeDto> Conflicts,
    [property: JsonPropertyName("upstream")] string? Upstream = null,
    [property: JsonPropertyName("ahead")] int Ahead = 0,
    [property: JsonPropertyName("behind")] int Behind = 0,
    [property: JsonPropertyName("isDetached")] bool IsDetached = false);

/// <summary>Single file change entry.</summary>
public sealed record GitFileChangeDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("oldPath")] string? OldPath = null,
    [property: JsonPropertyName("status")] string Status = "modified",
    [property: JsonPropertyName("staged")] bool Staged = false);

/// <summary>Branch item.</summary>
public sealed record GitBranchDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isRemote")] bool IsRemote = false,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent = false,
    [property: JsonPropertyName("isDefault")] bool IsDefault = false,
    [property: JsonPropertyName("tracking")] string? Tracking = null,
    [property: JsonPropertyName("ahead")] int Ahead = 0,
    [property: JsonPropertyName("behind")] int Behind = 0);

/// <summary>Commit item (history list).</summary>
public sealed record GitCommitDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("shortSha")] string ShortSha,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("authorEmail")] string AuthorEmail,
    [property: JsonPropertyName("authorDate")] string AuthorDate,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("body")] string? Body = null);

/// <summary>Single commit detail.</summary>
public sealed record GitCommitDetailDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("parents")] IReadOnlyList<string> Parents,
    [property: JsonPropertyName("changedFiles")] IReadOnlyList<GitFileChangeDto> ChangedFiles,
    [property: JsonPropertyName("body")] string? Body = null);

/// <summary>Single file diff text and stats.</summary>
public sealed record GitDiffDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("oldPath")] string? OldPath = null,
    [property: JsonPropertyName("patch")] string Patch = "",
    [property: JsonPropertyName("additions")] int Additions = 0,
    [property: JsonPropertyName("deletions")] int Deletions = 0,
    [property: JsonPropertyName("binary")] bool Binary = false,
    [property: JsonPropertyName("truncated")] bool Truncated = false);

/// <summary>Generic operation result for pull/push/merge/revert/checkout.</summary>
public sealed record GitOperationResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("operation")] string Operation = "",
    [property: JsonPropertyName("conflicts")] IReadOnlyList<string>? Conflicts = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("requiresCredentials")] bool RequiresCredentials = false);

/// <summary>Conflict file item.</summary>
public sealed record GitConflictFileDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] string Status = "conflicted",
    [property: JsonPropertyName("oursVersion")] string? OursVersion = null,
    [property: JsonPropertyName("theirsVersion")] string? TheirsVersion = null);

/// <summary>Single Git remote entry (origin/upstream/...).</summary>
public sealed record GitRemoteDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fetchUrl")] string FetchUrl,
    [property: JsonPropertyName("pushUrl")] string? PushUrl = null);

/// <summary>Probe result for an arbitrary path: is it a Git repo, current branch, configured remotes.</summary>
public sealed record GitRepositoryProbeDto(
    [property: JsonPropertyName("isRepository")] bool IsRepository,
    [property: JsonPropertyName("hasCommits")] bool HasCommits = false,
    [property: JsonPropertyName("currentBranch")] string? CurrentBranch = null,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch = null,
    [property: JsonPropertyName("remotes")] IReadOnlyList<GitRemoteDto>? Remotes = null);

/// <summary>Add or update a Git remote. <c>Name</c> is the remote name (origin/upstream/...);
/// <c>Url</c> is the fetch URL; <c>PushUrl</c> overrides the push URL if non-null.</summary>
public sealed record GitRemoteRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("pushUrl")] string? PushUrl = null);

// ── Request bodies ──

/// <summary>Register a new repository.</summary>
public sealed record GitRepositoryRegistration(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path);

/// <summary>Commit request.</summary>
public sealed record GitCommitRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
    [property: JsonPropertyName("amend")] bool Amend = false);

/// <summary>Create branch request.</summary>
public sealed record GitBranchCreateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("startPoint")] string? StartPoint = null,
    [property: JsonPropertyName("track")] bool Track = false);

/// <summary>Pull strategy request.</summary>
public sealed record GitPullRequest(
    [property: JsonPropertyName("strategy")] string Strategy = "merge",
    [property: JsonPropertyName("remote")] string? Remote = null,
    [property: JsonPropertyName("refspec")] string? Refspec = null);

/// <summary>Checkout request.</summary>
public sealed record GitCheckoutRequest(
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("createIfMissing")] bool CreateIfMissing = false);

/// <summary>Revert request.</summary>
public sealed record GitRevertRequest(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("noCommit")] bool NoCommit = false);

/// <summary>Resolve conflicts request.</summary>
public sealed record GitResolveRequest(
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
    [property: JsonPropertyName("continueMerge")] bool ContinueMerge = true);

// ── Git engine (host CLI) status & install ──

/// <summary>Host Git CLI availability probe result, analogous to <c>DockerStatusDto</c>.</summary>
public sealed record GitEngineStatusDto(
    [property: JsonPropertyName("isAvailable")] bool IsAvailable,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("executablePath")] string? ExecutablePath = null,
    [property: JsonPropertyName("canAutoInstall")] bool CanAutoInstall = false);

/// <summary>Auto-install response with operation status and incremental progress.</summary>
public sealed record GitEngineInstallResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null);
