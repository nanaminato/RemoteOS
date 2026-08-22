using System.Collections.ObjectModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Git;

namespace Client.Apps.Git;

public enum GitClientPage { Overview, Workspace, Log, ConflictResolution, Remotes }

/// <summary>State and typed operations for the Git Client. Uses DispatcherTimer (10s) for status refresh with Interlocked reentrancy guard.
/// Each window owns its own ViewModel instance — supports multiple projects open simultaneously (MultiWindow instance policy).</summary>
public sealed partial class GitClientViewModel(IRemoteGitClient client) : ObservableObject
{
    public ObservableCollection<GitRepositoryDto> Repositories { get; } = [];
    public ObservableCollection<GitBranchDto> Branches { get; } = [];
    public ObservableCollection<GitCommitDto> Commits { get; } = [];
    public ObservableCollection<GitFileChangeDto> StagedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> UnstagedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> UntrackedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> ConflictFiles { get; } = [];
    public ObservableCollection<GitRemoteDto> Remotes { get; } = [];
    public ObservableCollection<GitFileChangeDto> CommitChangedFiles { get; } = [];

    /// <summary>Files shown in the Changes list (union of unstaged + untracked, excluding .gitignored).</summary>
    public ObservableCollection<GitFileChangeItem> Changes { get; } = [];

    /// <summary>Number of selected files for commit.</summary>
    public int SelectedCount => Changes.Count(c => c.IsSelected);

    /// <summary>Gets the selected file paths for commit.</summary>
    public IReadOnlyList<string> SelectedFilePaths => Changes.Where(c => c.IsSelected).Select(c => c.Path).ToArray();

    [ObservableProperty] private GitRepositoryDto? _selectedRepository;
    [ObservableProperty] private GitClientPage _activePage = GitClientPage.Overview;
    [ObservableProperty] private GitStatusDto? _status;
    [ObservableProperty] private GitBranchDto? _selectedBranch;
    [ObservableProperty] private GitCommitDto? _selectedCommit;
    [ObservableProperty] private GitFileChangeDto? _selectedFile;
    [ObservableProperty] private GitDiffDto? _fileDiff;
    [ObservableProperty] private GitRemoteDto? _selectedRemote;
    [ObservableProperty] private string _statusText = "Loading…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAutoRefresh = true;
    [ObservableProperty] private string _commitMessage = string.Empty;
    [ObservableProperty] private bool _hasConflicts;
    [ObservableProperty] private GitCommitDetailDto? _commitDetail;
    [ObservableProperty] private string _branchSearchText = string.Empty;
    [ObservableProperty] private string _commitSearchText = string.Empty;
    [ObservableProperty] private GitFileChangeDto? _selectedCommitFile;

    // ── 项目选择器状态：IsPickerMode=true 时显示项目选择视图而非工作区 ──
    [ObservableProperty] private bool _isPickerMode = true;
    [ObservableProperty] private string _probeHint = string.Empty;
    [ObservableProperty] private bool _isProbing;

    // ── Host Git engine status & install flow (like DockerManagerViewModel) ──
    [ObservableProperty] private bool _isGitAvailable = true;
    [ObservableProperty] private bool _isGitInstallRequired;
    [ObservableProperty] private bool _canAutoInstall;
    [ObservableProperty] private string _engineVersion = "—";
    [ObservableProperty] private string _enginePath = "—";
    [ObservableProperty] private string _problemCode = string.Empty;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _installMessage = string.Empty;

    private DispatcherTimer? _timer;
    private int _refreshing;

    /// <summary>Dialog callbacks assigned by the app shell.</summary>
    public Func<Task<GitCommitRequest?>>? ShowCommitDialogAsync { get; set; }
    public Func<Task<GitBranchCreateRequest?>>? ShowCreateBranchDialogAsync { get; set; }
    public Func<Task<GitPullRequest?>>? ShowPullDialogAsync { get; set; }
    public Func<Task<GitRepositoryRegistration?>>? ShowRegisterRepositoryDialogAsync { get; set; }
    public Func<string, Task<bool>>? ShowConfirmAsync { get; set; }
    /// <summary>Assigned by the app shell so operations can surface an unavailable engine immediately.</summary>
    public Func<Task>? ShowGitUnavailableAsync { get; set; }

    /// <summary>Remote folder picker — opens an Explorer-like dialog and returns the selected server-side path, or null on cancel.</summary>
    public Func<Task<string?>>? ShowRemotePathPickerAsync { get; set; }
    /// <summary>Confirms with the user whether to initialize a Git repository at the supplied path.</summary>
    public Func<string, Task<bool>>? ShowInitConfirmAsync { get; set; }
    /// <summary>Prompts the user for new remote name + fetch URL (+ optional push URL).</summary>
    public Func<GitRemoteDto?, Task<GitRemoteRequest?>>? ShowRemoteDialogAsync { get; set; }
    /// <summary>Prompts the user for a new branch name when renaming. Returns null if user cancels.</summary>
    public Func<GitBranchDto, Task<string?>>? ShowRenameBranchDialogAsync { get; set; }
    /// <summary>Prompts the user for merge strategy (merge/no-ff/ff-only/squash) + optional message.
    /// The <paramref name="sourceBranch"/> argument is pre-filled so the dialog can show context.
    /// Returns null if user cancels.</summary>
    public Func<GitBranchDto, Task<GitMergeRequest?>>? ShowMergeDialogAsync { get; set; }
    /// <summary>Prompts the user for an upstream (remote/branch) to track, or choose "Unset" / auto <c>origin/{branch}</c>.
    /// Returns null if user cancels.</summary>
    public Func<GitBranchDto, Task<GitBranchTrackingRequest?>>? ShowSetTrackingDialogAsync { get; set; }

    public bool HasUpstream => Status?.Upstream is not null;
    public bool CanManage => SelectedRepository is not null && !IsBusy;
    public bool CanOpenProject => !IsBusy && IsPickerMode;

    public async Task StartAsync()
    {
        Log("StartAsync 开始");
        await RefreshEngineStatusAsync();
        Log($"引擎状态: IsAvailable={IsGitAvailable} Version={EngineVersion} Problem={ProblemCode}");
        if (!IsGitAvailable)
        {
            IsGitInstallRequired = IsInstallRequired(IsGitAvailable, ProblemCode);
            StatusText = "Git 未安装或不可用";
            if (ShowGitUnavailableAsync is not null)
                await ShowGitUnavailableAsync();
            if (!IsGitAvailable) return; // still unavailable after dialog → stop further init
        }

        await RefreshRepositoriesAsync();
        Log($"项目列表: Repositories.Count={Repositories.Count} IsPickerMode={IsPickerMode}");
        StatusText = IsPickerMode
            ? (Repositories.Count > 0 ? "请选择或打开一个项目" : "点击「打开文件夹」选择一个 Git 仓库")
            : "Ready";

        if (!IsPickerMode && IsAutoRefresh)
            StartStatusTimer();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void StartStatusTimer()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
    }

    /// <summary>Reloads the registered repository list (project picker source) without leaving picker mode.</summary>
    [RelayCommand]
    public async Task RefreshRepositoriesAsync()
    {
        Log("RefreshRepositoriesAsync 开始调用 client.ListRepositoriesAsync …");
        try
        {
            var repos = await client.ListRepositoriesAsync();
            Repositories.Clear();
            foreach (var repo in repos) Repositories.Add(repo);
            Log($"RefreshRepositoriesAsync 完成，项目数={Repositories.Count}");
        }
        catch (Exception ex)
        {
            StatusText = $"加载项目列表失败：{ex.Message}";
            Log($"RefreshRepositoriesAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
    }

    private async Task RefreshAllAsync()
    {
        if (SelectedRepository is null) { Log("RefreshAllAsync: SelectedRepository=null → 跳过"); return; }
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) { Log("RefreshAllAsync: 另一次刷新正在进行 → 跳过"); return; }
        try
        {
            Log("RefreshAllAsync: 并行请求 GetStatus / ListBranches / GetLog(100) …");
            var statusTask = client.GetStatusAsync(SelectedRepository.Id);
            var branchesTask = client.ListBranchesAsync(SelectedRepository.Id);
            var logTask = client.GetLogAsync(SelectedRepository.Id, limit: 100);

            Status = await statusTask;
            var branches = await branchesTask;
            var commits = await logTask;
            Log($"收到数据: Status.Branch={Status?.Branch ?? "(null)"} Branches={branches.Count} Commits={commits.Count}");

            Branches.Clear();
            foreach (var b in branches) Branches.Add(b);

            Commits.Clear();
            foreach (var c in commits) Commits.Add(c);

            StagedFiles.Clear();
            UnstagedFiles.Clear();
            UntrackedFiles.Clear();
            ConflictFiles.Clear();

            if (Status is not null)
            {
                foreach (var f in Status.Staged) StagedFiles.Add(f);
                foreach (var f in Status.Unstaged) UnstagedFiles.Add(f);
                foreach (var f in Status.Untracked) UntrackedFiles.Add(f);
                foreach (var f in Status.Conflicts) ConflictFiles.Add(f);
                HasConflicts = ConflictFiles.Count > 0;
                if (HasConflicts) ActivePage = GitClientPage.ConflictResolution;
                Log($"文件变更计数: Staged={StagedFiles.Count} Unstaged={UnstagedFiles.Count} " +
                    $"Untracked={UntrackedFiles.Count} Conflicts={ConflictFiles.Count}");
            }
            else
            {
                Log("⚠ Status 返回为 null — 工作区变更与分支信息无法呈现");
            }

            RebuildChangesList();
            StatusText = $"Ready — {Status?.Branch ?? "unknown"}";
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
            Log($"RefreshAllAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (SelectedRepository is null) return;
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try
        {
            Status = await client.GetStatusAsync(SelectedRepository.Id);
            StagedFiles.Clear();
            UnstagedFiles.Clear();
            UntrackedFiles.Clear();
            ConflictFiles.Clear();
            if (Status is not null)
            {
                foreach (var f in Status.Staged) StagedFiles.Add(f);
                foreach (var f in Status.Unstaged) UnstagedFiles.Add(f);
                foreach (var f in Status.Untracked) UntrackedFiles.Add(f);
                foreach (var f in Status.Conflicts) ConflictFiles.Add(f);
                HasConflicts = ConflictFiles.Count > 0;
            }
            RebuildChangesList();
        }
        catch { /* silent — timer tick */ }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [RelayCommand]
    private async Task RefreshEngineStatusAsync()
    {
        try
        {
            var status = await client.GetEngineStatusAsync();
            IsGitAvailable = status.IsAvailable;
            ProblemCode = status.ProblemCode ?? string.Empty;
            EngineVersion = string.IsNullOrWhiteSpace(status.Version) ? "—" : status.Version;
            EnginePath = string.IsNullOrWhiteSpace(status.ExecutablePath) ? "—" : status.ExecutablePath;
            CanAutoInstall = status.CanAutoInstall;
            IsGitInstallRequired = IsInstallRequired(IsGitAvailable, ProblemCode);
            InstallEngineCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            IsGitAvailable = false;
            ProblemCode = "error";
            EngineVersion = "—";
            EnginePath = "—";
            CanAutoInstall = false;
            IsGitInstallRequired = false;
            StatusText = $"检测 Git 状态失败：{ex.Message}";
        }
    }

    private bool CanInstallEngine => !IsInstalling && CanAutoInstall && !IsGitAvailable;

    [RelayCommand(CanExecute = nameof(CanInstallEngine))]
    private async Task InstallEngineAsync()
    {
        if (IsInstalling) return;
        IsInstalling = true;
        InstallMessage = "正在安装 Git，请稍候…";
        try
        {
            var result = await client.InstallEngineAsync();
            InstallMessage = result.Success ? "安装完成，正在验证…" : (result.Message ?? "安装失败");
            await RefreshEngineStatusAsync();
            if (result.Success && IsGitAvailable)
            {
                InstallMessage = "Git 已安装，可以继续使用。";
            }
        }
        catch (Exception ex)
        {
            InstallMessage = $"安装出错：{ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            InstallEngineCommand.NotifyCanExecuteChanged();
        }
    }

    private static bool IsInstallRequired(bool isAvailable, string problemCode)
    {
        if (isAvailable) return false;
        return string.Equals(problemCode, "not_installed", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RefreshAsync()
    {
        await RefreshAllAsync();
    }

    /// <summary>在项目选择器中点击已注册的项目，进入工作区。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenProjectAsync(GitRepositoryDto repo)
    {
        Log($"OpenProjectAsync 开始: name={repo?.Name} id={repo?.Id}");
        if (repo is null) { Log("repo 为 null → 退出"); return; }
        try
        {
            Log($"切换 IsPickerMode=false  SelectedRepository={repo.Name}");
            IsPickerMode = false;
            SelectedRepository = repo;
            ActivePage = GitClientPage.Overview;
            StatusText = $"Loading {repo.Name}…";

            Log("调用 RefreshAllAsync（并行 status/branches/log）…");
            await RefreshAllAsync();
            Log($"RefreshAllAsync 完成。Branches={Branches.Count} Commits={Commits.Count} " +
                $"Staged={StagedFiles.Count} Unstaged={UnstagedFiles.Count} Untracked={UntrackedFiles.Count}");

            Log("启动自动刷新（10s 轮询）…");
            StartStatusTimer();

            Log("调用 RefreshRemotesAsync…");
            await RefreshRemotesAsync();
            Log($"OpenProjectAsync 结束，Remotes={Remotes.Count}，Ready");
            StatusText = $"Ready — {repo.Name} @ {Status?.Branch ?? "unknown"}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开项目失败：{ex.Message}";
            Log($"OpenProjectAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>打开远程文件夹选择器；选中后探测 Git 状态：是仓库则注册并打开；否则提示初始化。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenFolderAsync()
    {
        Log("OpenFolderAsync 开始");
        if (ShowRemotePathPickerAsync is null)
        {
            StatusText = "路径选择器不可用（ShowRemotePathPickerAsync=null）";
            Log("ShowRemotePathPickerAsync 委托未设置");
            return;
        }

        var path = await ShowRemotePathPickerAsync();
        Log($"路径选择返回: {(path is null ? "null" : $"\"{path}\"")}");
        if (string.IsNullOrWhiteSpace(path)) return;

        await ProbeAndOpenAsync(path);
    }

    /// <summary>手动注册一个绝对路径作为 Git 项目（输入对话框形式）。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task RegisterRepositoryAsync()
    {
        Log("RegisterRepositoryAsync 开始");
        if (ShowRegisterRepositoryDialogAsync is null) { Log("ShowRegisterRepositoryDialogAsync=null"); return; }
        var registration = await ShowRegisterRepositoryDialogAsync();
        Log($"对话框返回: {(registration is null ? "null" : $"{registration.Name} @ {registration.Path}")}");
        if (registration is null) return;
        try
        {
            var dto = await client.RegisterRepositoryAsync(registration);
            if (!Repositories.Contains(dto))
                Repositories.Add(dto);
            StatusText = $"已注册项目「{dto.Name}」，正在打开…";
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex) { StatusText = $"注册失败：{ex.Message}"; Log(ex.ToString()); }
    }

    private async Task ProbeAndOpenAsync(string path)
    {
        Log($"ProbeAndOpenAsync path={path}");
        IsProbing = true;
        IsBusy = true;
        ProbeHint = $"正在检查 {path} …";
        try
        {
            Log("调用 client.ProbeRepositoryAsync …");
            var probe = await client.ProbeRepositoryAsync(path);
            Log($"Probe 返回: IsRepository={probe.IsRepository} HasCommits={probe.HasCommits} Branch={probe.CurrentBranch} Remotes={probe.Remotes?.Count ?? 0}");
            if (!probe.IsRepository)
            {
                ProbeHint = $"所选目录不是 Git 仓库";
                var init = ShowInitConfirmAsync is not null && await ShowInitConfirmAsync(path);
                Log($"ShowInitConfirm 返回: {init}");
                if (!init)
                {
                    StatusText = "已取消初始化";
                    return;
                }
                var initResult = await client.InitRepositoryAsync(path);
                Log($"git init 返回: Success={initResult.Success} Message={initResult.Message}");
                if (!initResult.Success)
                {
                    StatusText = $"git init 失败：{initResult.Message}";
                    return;
                }
                StatusText = "Git 仓库已初始化";
                probe = await client.ProbeRepositoryAsync(path);
                Log($"重探测后: IsRepository={probe.IsRepository} DefaultBranch={probe.DefaultBranch}");
            }

            // 已是 Git 仓库：检查是否已注册
            var existing = Repositories.FirstOrDefault(r =>
                string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Log($"路径已注册为「{existing.Name}」，直接打开");
                StatusText = $"项目已存在：{existing.Name}";
                await OpenProjectCommand.ExecuteAsync(existing);
                return;
            }

            // 未注册：自动注册（名称取路径末段）
            var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = path;
            Log($"自动注册: name={name} path={path}");
            var dto = await client.RegisterRepositoryAsync(new GitRepositoryRegistration(name, path));
            if (!Repositories.Contains(dto))
                Repositories.Add(dto);
            StatusText = $"项目「{dto.Name}」已注册";
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex)
        {
            StatusText = $"探测失败：{ex.Message}";
            ProbeHint = $"探测失败：{ex.Message}";
            Log($"ProbeAndOpenAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            IsProbing = false;
            IsBusy = false;
        }
    }

    /// <summary>写入 [TRACE] 日志到状态栏（便于用户从 UI 直接看到执行路径）与 Debug Output 窗口。</summary>
    private void Log(string message)
    {
        var line = $"[TRACE] {message}";
        StatusText = line;
        System.Diagnostics.Debug.WriteLine(line);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutAsync(GitBranchDto branch)
    {
        if (SelectedRepository is null || branch is null) return;
        IsBusy = true;
        StatusText = $"Checking out {branch.Name}…";
        try
        {
            var result = await client.CheckoutAsync(SelectedRepository.Id, new GitCheckoutRequest(branch.Name));
            if (result.Success)
            {
                StatusText = $"Switched to {branch.Name}";
                await RefreshAllAsync();
            }
            else
            {
                StatusText = $"Checkout failed: {result.Message}";
                if (result.Conflicts is not null && result.Conflicts.Count > 0)
                    ActivePage = GitClientPage.ConflictResolution;
            }
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CreateBranchAsync()
    {
        if (ShowCreateBranchDialogAsync is null || SelectedRepository is null) return;
        var request = await ShowCreateBranchDialogAsync();
        if (request is null) return;
        IsBusy = true;
        try
        {
            var result = await client.CreateBranchAsync(SelectedRepository.Id, request);
            StatusText = result.Success ? $"Branch '{request.Name}' created" : $"Failed: {result.Message}";
            await RefreshAllAsync();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task DeleteBranchAsync(GitBranchDto branch)
    {
        if (SelectedRepository is null || branch is null || branch.IsCurrent) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync($"Delete branch '{branch.Name}'?"))
            return;
        IsBusy = true;
        try
        {
            var result = await client.DeleteBranchAsync(SelectedRepository.Id, branch.Name);
            StatusText = result.Success ? $"Branch '{branch.Name}' deleted" : $"Failed: {result.Message}";
            await RefreshAllAsync();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CommitAsync()
    {
        if (SelectedRepository is null) return;
        
        // If a commit dialog is assigned, use it (for amend option etc.)
        if (ShowCommitDialogAsync is not null)
        {
            var request = await ShowCommitDialogAsync();
            if (request is null) return;
            IsBusy = true;
            try
            {
                var result = await client.CommitAsync(SelectedRepository.Id, request);
                StatusText = result.Success ? "Committed" : $"Commit failed: {result.Message}";
                await RefreshAllAsync();
            }
            catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
            finally { IsBusy = false; }
        }
        else
        {
            // Direct commit from workspace: use selected files
            if (SelectedCount == 0)
            {
                StatusText = "请先选择要提交的文件";
                return;
            }
            if (string.IsNullOrWhiteSpace(CommitMessage))
            {
                StatusText = "请输入提交消息";
                return;
            }
            IsBusy = true;
            try
            {
                var request = new GitCommitRequest(CommitMessage, SelectedFilePaths.ToArray());
                var result = await client.CommitAsync(SelectedRepository.Id, request);
                StatusText = result.Success ? "Committed" : $"Commit failed: {result.Message}";
                if (result.Success)
                    CommitMessage = string.Empty;
                await RefreshAllAsync();
            }
            catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
            finally { IsBusy = false; }
        }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PullAsync()
    {
        if (SelectedRepository is null) return;
        GitPullRequest? request = new();
        if (ShowPullDialogAsync is not null)
            request = await ShowPullDialogAsync();
        if (request is null) return;
        IsBusy = true;
        StatusText = "Pulling…";
        try
        {
            var result = await client.PullAsync(SelectedRepository.Id, request);
            if (result.RequiresCredentials)
                StatusText = "Git credentials required — configure on the host OS";
            else if (result.Success)
            {
                StatusText = "Pulled successfully";
                await RefreshAllAsync();
            }
            else
            {
                StatusText = $"Pull failed: {result.Message}";
                if (result.Conflicts is not null && result.Conflicts.Count > 0)
                    ActivePage = GitClientPage.ConflictResolution;
            }
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushAsync()
    {
        if (SelectedRepository is null) return;
        IsBusy = true;
        StatusText = "Pushing…";
        try
        {
            var result = await client.PushAsync(SelectedRepository.Id);
            if (result.RequiresCredentials)
                StatusText = "Git credentials required — configure on the host OS";
            else
            {
                StatusText = result.Success ? "Pushed successfully" : $"Push failed: {result.Message}";
                await RefreshAllAsync();
            }
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task FetchAsync()
    {
        if (SelectedRepository is null) return;
        IsBusy = true;
        try
        {
            var result = await client.FetchAsync(SelectedRepository.Id);
            StatusText = result.Success ? "Fetched" : (result.RequiresCredentials ? "Credentials required" : $"Failed: {result.Message}");
            await RefreshAllAsync();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ViewDiffAsync(GitFileChangeDto file)
    {
        if (SelectedRepository is null || file is null) return;
        try
        {
            FileDiff = await client.GetDiffAsync(SelectedRepository.Id, file.Path, file.Staged);
        }
        catch (Exception ex) { StatusText = $"Diff failed: {ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertAsync(GitCommitDto commit)
    {
        if (SelectedRepository is null || commit is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync($"Revert commit {commit.ShortSha}?"))
            return;
        IsBusy = true;
        try
        {
            var result = await client.RevertAsync(SelectedRepository.Id, new GitRevertRequest(commit.Sha));
            StatusText = result.Success ? "Reverted" : $"Revert failed: {result.Message}";
            if (result.Conflicts is not null && result.Conflicts.Count > 0)
                ActivePage = GitClientPage.ConflictResolution;
            await RefreshAllAsync();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StageFileAsync(GitFileChangeDto file)
    {
        if (SelectedRepository is null || file is null) return;
        try
        {
            await client.CommitAsync(SelectedRepository.Id, new GitCommitRequest("", [file.Path]));
            await RefreshStatusAsync();
        }
        catch (Exception ex) { StatusText = $"Stage failed: {ex.Message}"; }
    }

    // ── 工作区变更选择管理 ──

    /// <summary>Checks if a file path is selected for commit.</summary>
    public bool IsFileSelected(string path) => Changes.Any(c => c.Path == path && c.IsSelected);

    /// <summary>Toggles file selection for commit.</summary>
    public void ToggleFileSelection(GitFileChangeItem item)
    {
        item.IsSelected = !item.IsSelected;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    /// <summary>Sets file selection state.</summary>
    public void SetFileSelection(GitFileChangeItem item, bool isSelected)
    {
        item.IsSelected = isSelected;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    /// <summary>Selects all files in the Changes list.</summary>
    [RelayCommand]
    private void SelectAllChanges()
    {
        foreach (var c in Changes) c.IsSelected = true;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    /// <summary>Clears all selected files.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var c in Changes) c.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    /// <summary>Refreshes changes list (re-processes .gitignore rules by re-fetching status).</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RefreshChangesAsync()
    {
        await RefreshStatusAsync();
        StatusText = $"已刷新变更列表，共 {Changes.Count} 个文件";
    }

    /// <summary>Rebuilds the Changes list from UnstagedFiles + UntrackedFiles.</summary>
    private void RebuildChangesList()
    {
        // Unsubscribe old items
        foreach (var item in Changes)
            item.SelectionChanged -= OnItemSelectionChanged;

        Changes.Clear();
        foreach (var f in UnstagedFiles)
        {
            var item = new GitFileChangeItem(f);
            item.SelectionChanged += OnItemSelectionChanged;
            Changes.Add(item);
        }
        foreach (var f in UntrackedFiles)
        {
            if (!Changes.Any(c => c.Path == f.Path))
            {
                var item = new GitFileChangeItem(f);
                item.SelectionChanged += OnItemSelectionChanged;
                Changes.Add(item);
            }
        }
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    [RelayCommand]
    private void NavigateTo(GitClientPage page)
    {
        ActivePage = page;
        if (page == GitClientPage.Remotes)
            _ = RefreshRemotesAsync();
    }

    // ── 远程（remote）管理 ──

    private bool CanManageRemotes => SelectedRepository is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task RefreshRemotesAsync()
    {
        if (SelectedRepository is null) { Log("RefreshRemotesAsync: SelectedRepository=null → 跳过"); return; }
        Log($"RefreshRemotesAsync: 调用 client.ListRemotesAsync(RepoId={SelectedRepository.Id}) …");
        try
        {
            var remotes = await client.ListRemotesAsync(SelectedRepository.Id);
            Remotes.Clear();
            foreach (var r in remotes) Remotes.Add(r);
            Log($"加载远程完成: count={Remotes.Count} items=[{string.Join(",", remotes.Select(r => $"{r.Name}={r.FetchUrl}"))}]");
            if (Remotes.Count > 0) SelectedRemote = Remotes[0];
            else SelectedRemote = null;
        }
        catch (Exception ex)
        {
            StatusText = $"加载远程列表失败：{ex.Message}";
            Log($"RefreshRemotesAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task AddRemoteAsync()
    {
        if (SelectedRepository is null || ShowRemoteDialogAsync is null) return;
        var request = await ShowRemoteDialogAsync(null);
        if (request is null) return;
        try
        {
            var result = await client.AddRemoteAsync(SelectedRepository.Id, request);
            StatusText = result.Success ? $"远程「{request.Name}」已添加" : $"添加失败：{result.Message}";
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { StatusText = $"添加失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task EditRemoteAsync(GitRemoteDto remote)
    {
        if (SelectedRepository is null || remote is null || ShowRemoteDialogAsync is null) return;
        var request = await ShowRemoteDialogAsync(remote);
        if (request is null) return;
        try
        {
            var result = await client.UpdateRemoteAsync(SelectedRepository.Id, remote.Name, request);
            StatusText = result.Success ? $"远程「{request.Name}」已更新" : $"更新失败：{result.Message}";
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { StatusText = $"更新失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task RemoveRemoteAsync(GitRemoteDto remote)
    {
        if (SelectedRepository is null || remote is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync($"删除远程「{remote.Name}」？"))
            return;
        try
        {
            var result = await client.RemoveRemoteAsync(SelectedRepository.Id, remote.Name);
            StatusText = result.Success ? $"远程「{remote.Name}」已删除" : $"删除失败：{result.Message}";
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { StatusText = $"删除失败：{ex.Message}"; }
    }

    // ── 选中提交变化：加载提交详情（含变更文件列表）──
    partial void OnSelectedCommitChanged(GitCommitDto? value)
    {
        _ = LoadCommitDetailAsync(value);
    }

    private async Task LoadCommitDetailAsync(GitCommitDto? commit)
    {
        CommitChangedFiles.Clear();
        CommitDetail = null;
        if (commit is null || SelectedRepository is null) return;
        try
        {
            var detail = await client.GetCommitDetailAsync(SelectedRepository.Id, commit.Sha);
            CommitDetail = detail;
            foreach (var f in detail.ChangedFiles) CommitChangedFiles.Add(f);
        }
        catch (Exception ex)
        {
            // 服务端尚未实现该端点时，降级为仅展示提交信息，不崩溃
            StatusText = $"加载提交详情失败：{ex.Message}（接口可能未实现）";
        }
    }

    // ── 分支右键菜单命令 ──

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutBranchAsync(GitBranchDto? branch)
    {
        if (branch is null) branch = SelectedBranch;
        if (branch is null) return;
        await CheckoutAsync(branch);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CreateBranchFromHereAsync(GitBranchDto? baseBranch)
    {
        if (ShowCreateBranchDialogAsync is null || SelectedRepository is null) return;
        var request = await ShowCreateBranchDialogAsync();
        if (request is null) return;
        // 若用户对话框未指定起点，则以右键选中的分支作为起点
        var startPoint = string.IsNullOrWhiteSpace(request.StartPoint) && baseBranch is not null
            ? baseBranch.Name
            : request.StartPoint;
        IsBusy = true;
        try
        {
            var result = await client.CreateBranchAsync(SelectedRepository.Id,
                request with { StartPoint = startPoint });
            StatusText = result.Success ? $"分支「{request.Name}」已创建" : $"创建失败：{result.Message}";
            await RefreshAllAsync();
        }
        catch (Exception ex) { StatusText = $"创建失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task DeleteBranchContextAsync(GitBranchDto? branch)
    {
        if (branch is null) branch = SelectedBranch;
        if (branch is null) return;
        await DeleteBranchAsync(branch);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RenameBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || SelectedRepository is null) return;
        if (branch.IsRemote) { StatusText = "不能直接重命名远程分支，请先在本地重命名后推送。"; return; }

        // 弹输入框取新名称；对话框未接入壳时走二次确认+占位提示（服务端接口已就绪）
        string? newName = null;
        if (ShowRenameBranchDialogAsync is not null)
            newName = await ShowRenameBranchDialogAsync(branch);
        else if (ShowConfirmAsync is not null)
        {
            // 壳暂未注入重命名输入对话框时，降级为直接尝试一个合理默认行为：追加 "-2" 后缀（便于先跑通接口链路）
            if (!await ShowConfirmAsync($"重命名分支「{branch.Name}」？（壳暂未接入输入对话框，将自动追加 \"-2\" 后缀，如需自定义名称请稍后重试）"))
                return;
            newName = $"{branch.Name}-2";
        }

        if (string.IsNullOrWhiteSpace(newName)) return;
        IsBusy = true;
        try
        {
            var result = await client.RenameBranchAsync(SelectedRepository.Id, branch.Name,
                new GitBranchRenameRequest(newName));
            if (result.Success)
            {
                StatusText = $"已将「{branch.Name}」重命名为「{newName}」";
                await RefreshAllAsync();
            }
            else
            {
                StatusText = $"重命名失败：{result.Message}";
            }
        }
        catch (Exception ex) { StatusText = $"重命名失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task MergeBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || SelectedRepository is null) return;

        GitMergeRequest? request;
        if (ShowMergeDialogAsync is not null)
        {
            request = await ShowMergeDialogAsync(branch);
            if (request is null) return;
        }
        else
        {
            // 默认策略：普通 merge（非 ff-only / squash），让 git 根据仓库配置决定是否生成合并提交
            request = new GitMergeRequest(branch.Name);
            if (ShowConfirmAsync is not null
                && !await ShowConfirmAsync($"将分支「{branch.Name}」合并到当前（使用默认 merge 策略）？"))
                return;
        }

        IsBusy = true;
        try
        {
            var result = await client.MergeBranchAsync(SelectedRepository.Id, request);
            if (result.Conflicts is not null && result.Conflicts.Count > 0)
            {
                StatusText = $"合并产生 {result.Conflicts.Count} 个冲突，请先解决。";
                HasConflicts = true;
                ActivePage = GitClientPage.ConflictResolution;
                await RefreshAllAsync();
            }
            else if (result.Success)
            {
                StatusText = $"已合并「{request.Source}」";
                await RefreshAllAsync();
            }
            else
            {
                StatusText = $"合并失败：{result.Message}";
            }
        }
        catch (Exception ex) { StatusText = $"合并失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RebaseBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null) return;
        StatusText = $"变基分支「{branch.Name}」：功能待实现（需新增 rebase 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null) return;
        if (branch.IsRemote) { StatusText = "不能推送远程分支对象"; return; }
        await PushAsync();
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PullBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null) return;
        await PullAsync();
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task FetchBranchAsync(GitBranchDto? _) => await FetchAsync();

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SetUpstreamBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || SelectedRepository is null) return;
        if (branch.IsRemote) { StatusText = "不能为远程分支对象设置跟踪；请选择本地分支。"; return; }

        GitBranchTrackingRequest? request;
        if (ShowSetTrackingDialogAsync is not null)
        {
            request = await ShowSetTrackingDialogAsync(branch);
            if (request is null) return;
        }
        else
        {
            // 默认：若分支名形式为 "origin/foo" 则尝试匹配远程分支；否则自动绑定 origin/<same-name>
            var prompt = $"为本地分支「{branch.Name}」设置跟踪远程 origin/{branch.Name}？";
            if (ShowConfirmAsync is not null && !await ShowConfirmAsync(prompt + "（如存在则绑定成功，不存在会返回错误，稍后可在对话框中自定义）"))
                return;
            request = new GitBranchTrackingRequest(Remote: "origin", Branch: branch.Name);
        }

        IsBusy = true;
        try
        {
            var result = await client.SetBranchTrackingAsync(SelectedRepository.Id, branch.Name, request);
            if (result.Success)
            {
                var unset = string.IsNullOrWhiteSpace(request.Upstream)
                            && string.IsNullOrWhiteSpace(request.Remote)
                            && string.IsNullOrWhiteSpace(request.Branch);
                StatusText = unset
                    ? $"已取消「{branch.Name}」的跟踪分支"
                    : $"已设置「{branch.Name}」跟踪 {(string.IsNullOrWhiteSpace(request.Upstream) ? $"{request.Remote}/{request.Branch}" : request.Upstream)}";
                await RefreshAllAsync();
            }
            else
            {
                StatusText = $"设置跟踪失败：{result.Message}";
            }
        }
        catch (Exception ex) { StatusText = $"设置跟踪失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── 提交右键菜单命令 ──

    [RelayCommand]
    private async Task CopyShaAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.Clipboard is not null)
                {
                    await topLevel.Clipboard.SetTextAsync(commit.Sha);
                    StatusText = $"已复制 SHA：{commit.ShortSha}";
                    return;
                }
            }
            // 兜底：通过 Dispatcher + TopLevel 遍历查找
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var tl = Avalonia.Controls.TopLevel.GetTopLevel((Avalonia.Visual?)null);
                if (tl?.Clipboard is not null) await tl.Clipboard.SetTextAsync(commit.Sha);
            });
            StatusText = $"SHA：{commit.ShortSha}（剪贴板复制暂未获取到 TopLevel）";
        }
        catch (Exception ex) { StatusText = $"复制失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task CopyShortShaAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.Clipboard is not null)
                {
                    await topLevel.Clipboard.SetTextAsync(commit.ShortSha);
                    StatusText = $"已复制短 SHA：{commit.ShortSha}";
                    return;
                }
            }
            StatusText = $"短 SHA：{commit.ShortSha}";
        }
        catch (Exception ex) { StatusText = $"复制失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null || SelectedRepository is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync($"签出提交 {commit.ShortSha}（将进入 detached HEAD 状态）？"))
            return;
        IsBusy = true;
        try
        {
            var result = await client.CheckoutAsync(SelectedRepository.Id, new GitCheckoutRequest(commit.Sha));
            StatusText = result.Success ? $"已签出 {commit.ShortSha}" : $"签出失败：{result.Message}";
            await RefreshAllAsync();
        }
        catch (Exception ex) { StatusText = $"签出失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ResetToCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        // TODO: 服务端尚未实现 git reset 端点（--soft/--mixed/--hard）
        StatusText = $"重置到 {commit.ShortSha}：功能待实现（需新增 reset 端点，注意 --hard 为危险操作）";
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertCommitContextAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await RevertAsync(commit);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task UndoCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        // TODO: 相当于 git reset --soft HEAD^ 或指定提交
        StatusText = $"撤销提交 {commit.ShortSha}：功能待实现（需新增 reset --soft 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CreatePatchFromCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        StatusText = $"从 {commit.ShortSha} 创建补丁：功能待实现（需新增 format-patch 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CherryPickCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        StatusText = $"Cherry-pick {commit.ShortSha}：功能待实现（需新增 cherry-pick 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RebaseInteractiveFromHereAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        StatusText = $"从 {commit.ShortSha} 开始交互式变基：功能待实现（需新增 rebase -i 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SquashFromHereAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        StatusText = $"压缩到 {commit.ShortSha}：功能待实现（交互式变基子集）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task EditCommitMessageAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        StatusText = $"编辑提交信息 {commit.ShortSha}：功能待实现（reword / amend 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushAllBeforeAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        StatusText = $"推送此前提交（含 {commit.ShortSha}）：功能待实现（指定 ref 推送端点）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CreateTagFromCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        StatusText = $"在 {commit.ShortSha} 新建标签：功能待实现（tag 管理端点）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CreateBranchAtCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null || ShowCreateBranchDialogAsync is null || SelectedRepository is null) return;
        var request = await ShowCreateBranchDialogAsync();
        if (request is null) return;
        var startPoint = string.IsNullOrWhiteSpace(request.StartPoint) ? commit.Sha : request.StartPoint;
        IsBusy = true;
        try
        {
            var result = await client.CreateBranchAsync(SelectedRepository.Id,
                request with { StartPoint = startPoint });
            StatusText = result.Success ? $"分支「{request.Name}」已创建于 {commit.ShortSha}" : $"创建失败：{result.Message}";
            await RefreshAllAsync();
        }
        catch (Exception ex) { StatusText = $"创建失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── 文件（工作区/提交详情）右键菜单命令 ──

    [RelayCommand]
    private async Task ShowFileDiffContextAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        await ViewDiffAsync(file);
    }

    [RelayCommand]
    private async Task ShowCommitFileDiffAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile;
        if (file is null || SelectedRepository is null || SelectedCommit is null) return;
        try
        {
            // 使用 ref=sha 查询提交版本的 diff
            FileDiff = await client.GetDiffAsync(SelectedRepository.Id, file.Path, staged: false, @ref: SelectedCommit.Sha);
        }
        catch (Exception ex) { StatusText = $"Diff 失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task OpenFileInEditorAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        // TODO: 通过 RemoteOS 内置 CodeEditor 或宿主 OS 默认编辑器打开，当前占位
        StatusText = $"打开文件「{file.Path}」：功能待实现（调用 CodeEditor / OpenWith 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertFileChangeAsync(GitFileChangeDto? file)
    {
        file ??= SelectedFile;
        if (file is null || SelectedRepository is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync($"还原文件「{file.Path}」的未提交变更？该操作不可撤销。"))
            return;
        // TODO: 服务端尚未实现 git checkout -- <path> / restore 端点
        StatusText = $"还原文件「{file.Path}」：功能待实现（需新增 restore 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task StageFileContextAsync(GitFileChangeDto? file)
    {
        file ??= SelectedFile;
        if (file is null) return;
        await StageFileAsync(file);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task UnstageFileAsync(GitFileChangeDto? file)
    {
        file ??= SelectedFile;
        if (file is null || SelectedRepository is null) return;
        // TODO: 服务端尚未实现 git reset HEAD <path> 端点
        StatusText = $"取消暂存「{file.Path}」：功能待实现（需新增 unstage 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ShowFileHistoryAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        StatusText = $"「{file.Path}」的历史记录：功能待实现（git log -- <path> 端点）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CreatePatchFromFileAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        StatusText = $"从「{file.Path}」创建补丁：功能待实现";
        await Task.CompletedTask;
    }
}
