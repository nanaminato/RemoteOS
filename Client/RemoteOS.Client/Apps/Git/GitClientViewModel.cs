using System.Collections.ObjectModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Client.Localization;
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

    /// <summary>Tracked files with modifications (already in version control).</summary>
    public ObservableCollection<GitFileChangeItem> TrackedChanges { get; } = [];

    /// <summary>Untracked files (new, not yet in version control).</summary>
    public ObservableCollection<GitFileChangeItem> UntrackedChanges { get; } = [];

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
    [ObservableProperty] private string _statusText = LocalizedText.Get("git.status.loading");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAutoRefresh = true;
    [ObservableProperty] private string _commitMessage = string.Empty;
    [ObservableProperty] private bool _hasConflicts;
    [ObservableProperty] private GitCommitDetailDto? _commitDetail;
    [ObservableProperty] private string _branchSearchText = string.Empty;
    [ObservableProperty] private string _commitSearchText = string.Empty;
    [ObservableProperty] private GitFileChangeDto? _selectedCommitFile;

    // ── Push dialog state ──
    public ObservableCollection<GitCommitDto> PushCommits { get; } = [];
    public ObservableCollection<GitFileChangeDto> PushChangedFiles { get; } = [];

    [ObservableProperty] private GitCommitDto? _pushSelectedCommit;
    [ObservableProperty] private string _pushSelectedRemote = string.Empty;
    [ObservableProperty] private string _pushSelectedBranch = string.Empty;
    [ObservableProperty] private string _pushLocalBranchName = string.Empty;
    [ObservableProperty] private bool _pushIsLoading;
    [ObservableProperty] private string _pushStatusMessage = string.Empty;

    public bool PushSingleCommitMode => PushSelectedCommit is not null || PushCommits.Count <= 1;
    public string PushBranchLineText => string.IsNullOrWhiteSpace(PushSelectedRemote) || string.IsNullOrWhiteSpace(PushSelectedBranch)
        ? string.Empty
        : LocalizedText.Format("git.dialog.push.branch_line_format", PushLocalBranchName, PushSelectedRemote, PushSelectedBranch);
    public int PushFileCount => PushChangedFiles.Count;
    public int PushCommitCount => PushCommits.Count;
    public bool PushHasCommits => PushCommits.Count > 0;

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
    /// <summary>Displays a modal message box with a single OK button. Used for error/validation
    /// reminders that must grab the user's attention (rather than being silently tucked into StatusText).</summary>
    public Func<string, Task>? ShowMessageAsync { get; set; }

    /// <summary>Shows the push preview dialog with commit list and file changes.
    /// Returns true if user confirms push, false if cancelled.</summary>
    public Func<Task<bool>>? ShowPushDialogAsync { get; set; }

    /// <summary>Shows the remote/branch picker dialog for push target selection.
    /// Input: current remote name (may be null), current branch name.
    /// Returns: (remoteName, branchName) tuple or null on cancel.</summary>
    public Func<string?, string?, Task<(string Remote, string Branch)?>>? ShowRemoteBranchPickerDialogAsync { get; set; }

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
            StatusText = LocalizedText.Get("git.vm.git_unavailable");
            if (ShowGitUnavailableAsync is not null)
                await ShowGitUnavailableAsync();
            if (!IsGitAvailable) return; // still unavailable after dialog → stop further init
        }

        await RefreshRepositoriesAsync();
        Log($"项目列表: Repositories.Count={Repositories.Count} IsPickerMode={IsPickerMode}");
        StatusText = IsPickerMode
            ? (Repositories.Count > 0 ? LocalizedText.Get("git.status.select_project") : LocalizedText.Get("git.status.click_open_folder"))
            : LocalizedText.Get("git.status.ready");

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
            await NotifyAsync(LocalizedText.Format("git.vm.load_repositories_failed_format", ex.Message));
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
            StatusText = LocalizedText.Format("git.status.ready_branch_format", Status?.Branch ?? LocalizedText.Get("git.status.unknown_branch"));
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Format("git.vm.refresh_failed_format", ex.Message);
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
            StatusText = LocalizedText.Format("git.vm.engine_check_failed_format", ex.Message);
        }
    }

    private bool CanInstallEngine => !IsInstalling && CanAutoInstall && !IsGitAvailable;

    [RelayCommand(CanExecute = nameof(CanInstallEngine))]
    private async Task InstallEngineAsync()
    {
        if (IsInstalling) return;
        IsInstalling = true;
        InstallMessage = LocalizedText.Get("git.vm.install_in_progress");
        try
        {
            var result = await client.InstallEngineAsync();
            InstallMessage = result.Success ? LocalizedText.Get("git.vm.install_verifying") : (result.Message ?? LocalizedText.Get("git.vm.install_failed"));
            await RefreshEngineStatusAsync();
            if (result.Success && IsGitAvailable)
            {
                InstallMessage = LocalizedText.Get("git.vm.installed");
            }
        }
        catch (Exception ex)
        {
            InstallMessage = LocalizedText.Format("git.vm.install_error_format", ex.Message);
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
            StatusText = LocalizedText.Format("git.vm.loading_project_format", repo.Name);

            Log("调用 RefreshAllAsync（并行 status/branches/log）…");
            await RefreshAllAsync();
            Log($"RefreshAllAsync 完成。Branches={Branches.Count} Commits={Commits.Count} " +
                $"Staged={StagedFiles.Count} Unstaged={UnstagedFiles.Count} Untracked={UntrackedFiles.Count}");

            Log("启动自动刷新（10s 轮询）…");
            StartStatusTimer();

            Log("调用 RefreshRemotesAsync…");
            await RefreshRemotesAsync();
            Log($"OpenProjectAsync 结束，Remotes={Remotes.Count}，Ready");
            StatusText = LocalizedText.Format("git.vm.ready_project_branch_format", repo.Name, Status?.Branch ?? LocalizedText.Get("git.status.unknown_branch"));
        }
        catch (Exception ex)
        {
            await NotifyAsync(LocalizedText.Format("git.vm.open_project_failed_format", ex.Message));
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
            await NotifyAsync(LocalizedText.Get("git.vm.path_picker_unavailable"));
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
            StatusText = LocalizedText.Format("git.vm.registered_format", dto.Name);
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.register_failed_format", ex.Message)); Log(ex.ToString()); }
    }

    private async Task ProbeAndOpenAsync(string path)
    {
        Log($"ProbeAndOpenAsync path={path}");
        IsProbing = true;
        IsBusy = true;
        ProbeHint = LocalizedText.Format("git.picker.probing_format", path);
        try
        {
            Log("调用 client.ProbeRepositoryAsync …");
            var probe = await client.ProbeRepositoryAsync(path);
            Log($"Probe 返回: IsRepository={probe.IsRepository} HasCommits={probe.HasCommits} Branch={probe.CurrentBranch} Remotes={probe.Remotes?.Count ?? 0}");
            if (!probe.IsRepository)
            {
                ProbeHint = LocalizedText.Get("git.vm.not_repo");
                var init = ShowInitConfirmAsync is not null && await ShowInitConfirmAsync(path);
                Log($"ShowInitConfirm 返回: {init}");
                if (!init)
                {
                    StatusText = LocalizedText.Get("git.vm.init_cancelled");
                    return;
                }
                var initResult = await client.InitRepositoryAsync(path);
                Log($"git init 返回: Success={initResult.Success} Message={initResult.Message}");
                if (!initResult.Success)
                {
                    await NotifyAsync(LocalizedText.Format("git.vm.init_failed_format", initResult.Message));
                    return;
                }
                StatusText = LocalizedText.Get("git.vm.initialized");
                probe = await client.ProbeRepositoryAsync(path);
                Log($"重探测后: IsRepository={probe.IsRepository} DefaultBranch={probe.DefaultBranch}");
            }

            // 已是 Git 仓库：检查是否已注册
            var existing = Repositories.FirstOrDefault(r =>
                string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Log($"路径已注册为「{existing.Name}」，直接打开");
                StatusText = LocalizedText.Format("git.vm.project_exists_format", existing.Name);
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
            StatusText = LocalizedText.Format("git.vm.project_registered_format", dto.Name);
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex)
        {
            await NotifyAsync(LocalizedText.Format("git.vm.probe_failed_format", ex.Message));
            ProbeHint = LocalizedText.Format("git.vm.probe_failed_format", ex.Message);
            Log($"ProbeAndOpenAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            IsProbing = false;
            IsBusy = false;
        }
    }

    /// <summary>Writes [TRACE] logs to the Debug Output window only — does not touch user-facing StatusText.
    /// StatusText should be set via LocalizedText.Get/Format so it remains localized and stable.</summary>
    private void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[TRACE] {message}");
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutAsync(GitBranchDto branch)
    {
        if (SelectedRepository is null || branch is null) return;
        IsBusy = true;
        StatusText = LocalizedText.Format("git.vm.checkout_progress_format", branch.Name);
        try
        {
            var result = await client.CheckoutAsync(SelectedRepository.Id, new GitCheckoutRequest(branch.Name));
            if (result.Success)
            {
                StatusText = LocalizedText.Format("git.vm.switched_to_format", branch.Name);
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.checkout_failed_format", result.Message));
                if (result.Conflicts is not null && result.Conflicts.Count > 0)
                    ActivePage = GitClientPage.ConflictResolution;
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
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
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.branch_created_format", request.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task DeleteBranchAsync(GitBranchDto branch)
    {
        if (SelectedRepository is null || branch is null || branch.IsCurrent) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.delete_confirm_format", branch.Name)))
            return;
        IsBusy = true;
        try
        {
            var result = await client.DeleteBranchAsync(SelectedRepository.Id, branch.Name);
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.branch_deleted_format", branch.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CommitAsync()
    {
        if (SelectedRepository is null) return;

        // 工作区已勾选文件且输入了提交消息 → 直接提交，不再弹对话框
        if (SelectedCount > 0 && !string.IsNullOrWhiteSpace(CommitMessage))
        {
            await CommitDirectAsync(amend: false);
            return;
        }

        // 兜底：工作区没输入消息或没勾选文件时，回退到对话框
        if (ShowCommitDialogAsync is not null)
        {
            var request = await ShowCommitDialogAsync();
            if (request is null) return;
            IsBusy = true;
            try
            {
                var result = await client.CommitAsync(SelectedRepository.Id, request);
                if (result.Success)
                {
                    StatusText = LocalizedText.Get("git.status.committed");
                    CommitMessage = string.Empty;
                    await RefreshAllAsync();
                    await ShowPushDialogAfterCommitAsync();
                }
                else
                {
                    await NotifyAsync(LocalizedText.Format("git.status.commit_failed_format", result.Message));
                }
            }
            catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
            finally { IsBusy = false; }
        }
        else
        {
            if (SelectedCount == 0)
                await NotifyAsync(LocalizedText.Get("git.status.no_files_selected"));
            else if (string.IsNullOrWhiteSpace(CommitMessage))
                await NotifyAsync(LocalizedText.Get("git.status.commit_message_required"));
        }
    }

    /// <summary>使用工作区已勾选的文件和已输入的提交消息直接发起提交，不经过对话框。
    /// 仅在已具备这两个前置条件时调用，调用方需自行校验。</summary>
    private async Task CommitDirectAsync(bool amend)
    {
        if (SelectedRepository is null) return;
        if (SelectedCount == 0)
        {
            await NotifyAsync(LocalizedText.Get("git.status.no_files_selected"));
            return;
        }
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            await NotifyAsync(LocalizedText.Get("git.status.commit_message_required"));
            return;
        }
        IsBusy = true;
        try
        {
            var request = new GitCommitRequest(CommitMessage, SelectedFilePaths.ToArray(), amend);
            var result = await client.CommitAsync(SelectedRepository.Id, request);
            if (result.Success)
            {
                StatusText = LocalizedText.Get("git.status.committed");
                CommitMessage = string.Empty;
                await RefreshAllAsync();
                await ShowPushDialogAfterCommitAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.status.commit_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    /// <summary>弹出单按钮模态消息框。优先使用注入的 <see cref="ShowMessageAsync"/>，
    /// 未注入时降级为写入 StatusText，保证逻辑链路不被打断。</summary>
    private async Task NotifyAsync(string message)
    {
        StatusText = message; // 同步写入状态栏，便于对话框关闭后用户仍能查阅
        if (ShowMessageAsync is not null)
            await ShowMessageAsync(message);
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
        StatusText = LocalizedText.Get("git.vm.pull_progress");
        try
        {
            var result = await client.PullAsync(SelectedRepository.Id, request);
            if (result.RequiresCredentials)
                await NotifyAsync(LocalizedText.Get("git.vm.credentials_required"));
            else if (result.Success)
            {
                StatusText = LocalizedText.Get("git.vm.pulled");
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.pull_failed_format", result.Message));
                if (result.Conflicts is not null && result.Conflicts.Count > 0)
                    ActivePage = GitClientPage.ConflictResolution;
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushAsync()
    {
        if (SelectedRepository is null) return;
        IsBusy = true;
        StatusText = LocalizedText.Get("git.vm.push_progress");
        try
        {
            var result = await client.PushAsync(SelectedRepository.Id);
            if (result.RequiresCredentials)
                await NotifyAsync(LocalizedText.Get("git.vm.credentials_required"));
            else
            {
                if (result.Success)
                    StatusText = LocalizedText.Get("git.vm.pushed");
                else
                    await NotifyAsync(LocalizedText.Format("git.vm.push_failed_format", result.Message));
                await RefreshAllAsync();
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
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
            if (result.Success)
                StatusText = LocalizedText.Get("git.vm.fetched");
            else if (result.RequiresCredentials)
                await NotifyAsync(LocalizedText.Get("git.vm.credentials_required_short"));
            else
                await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
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
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.diff_failed_format", ex.Message)); }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertAsync(GitCommitDto commit)
    {
        if (SelectedRepository is null || commit is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.revert_confirm_format", commit.ShortSha)))
            return;
        IsBusy = true;
        try
        {
            var result = await client.RevertAsync(SelectedRepository.Id, new GitRevertRequest(commit.Sha));
            if (result.Success)
                StatusText = LocalizedText.Get("git.vm.reverted");
            else
                await NotifyAsync(LocalizedText.Format("git.vm.revert_failed_format", result.Message));
            if (result.Conflicts is not null && result.Conflicts.Count > 0)
                ActivePage = GitClientPage.ConflictResolution;
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
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
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.stage_failed_format", ex.Message)); }
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
        StatusText = LocalizedText.Format("git.status.refreshed_changes", Changes.Count);
    }

    /// <summary>Rebuilds the Changes list from UnstagedFiles + UntrackedFiles, preserving selection state.</summary>
    private void RebuildChangesList()
    {
        // Save currently selected paths before rebuilding
        var selectedPaths = new HashSet<string>(Changes.Where(c => c.IsSelected).Select(c => c.Path));

        // Unsubscribe old items
        foreach (var item in Changes)
            item.SelectionChanged -= OnItemSelectionChanged;

        Changes.Clear();
        TrackedChanges.Clear();
        UntrackedChanges.Clear();
        
        // Add unstaged files (tracked files with modifications)
        foreach (var f in UnstagedFiles)
        {
            var item = new GitFileChangeItem(f, selectedPaths.Contains(f.Path));
            item.SelectionChanged += OnItemSelectionChanged;
            Changes.Add(item);
            TrackedChanges.Add(item);
        }
        
        // Add untracked files (new files not yet in version control)
        foreach (var f in UntrackedFiles)
        {
            if (!Changes.Any(c => c.Path == f.Path))
            {
                var item = new GitFileChangeItem(f, selectedPaths.Contains(f.Path));
                item.SelectionChanged += OnItemSelectionChanged;
                Changes.Add(item);
                UntrackedChanges.Add(item);
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
            await NotifyAsync(LocalizedText.Format("git.vm.load_remotes_failed_format", ex.Message));
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
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.add_remote_format", request.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.add_remote_failed_format", result.Message));
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.add_remote_failed_format", ex.Message)); }
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
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.update_remote_format", request.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.update_remote_failed_format", result.Message));
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.update_remote_failed_format", ex.Message)); }
    }

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task RemoveRemoteAsync(GitRemoteDto remote)
    {
        if (SelectedRepository is null || remote is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.delete_remote_confirm_format", remote.Name)))
            return;
        try
        {
            var result = await client.RemoveRemoteAsync(SelectedRepository.Id, remote.Name);
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.delete_remote_format", remote.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.delete_remote_failed_format", result.Message));
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.delete_remote_failed_format", ex.Message)); }
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
            StatusText = LocalizedText.Format("git.vm.load_commit_detail_failed_format", ex.Message);
        }
    }

    // ── Push dialog state management ──

    /// <summary>Prepares the push dialog state by loading ahead commits and setting defaults.
    /// Called before showing the push dialog, or when navigating the dialog.</summary>
    public async Task PreparePushPreviewAsync()
    {
        if (SelectedRepository is null || Status is null) return;

        PushIsLoading = true;
        PushStatusMessage = LocalizedText.Get("git.dialog.push.loading_commits");

        try
        {
            PushCommits.Clear();
            PushChangedFiles.Clear();
            PushSelectedCommit = null;

            PushLocalBranchName = Status.Branch;

            var upstream = Status.Upstream;
            if (!string.IsNullOrWhiteSpace(upstream))
            {
                var parts = upstream.Split('/');
                if (parts.Length >= 2)
                {
                    PushSelectedRemote = parts[0];
                    PushSelectedBranch = string.Join("/", parts.Skip(1));
                }
                else
                {
                    PushSelectedRemote = "origin";
                    PushSelectedBranch = upstream;
                }
            }
            else
            {
                PushSelectedRemote = Remotes.Count > 0 ? Remotes[0].Name : "origin";
                PushSelectedBranch = Status.Branch;
            }

            if (Status.Ahead > 0)
            {
                var commits = await client.GetLogAsync(SelectedRepository.Id, limit: Status.Ahead + 50);
                var ahead = commits.Take(Status.Ahead).ToList();
                foreach (var c in ahead) PushCommits.Add(c);
            }

            PushStatusMessage = PushCommits.Count == 0
                ? LocalizedText.Get("git.dialog.push.no_commits_ahead")
                : LocalizedText.Format("git.vm.push_n_commits_ahead_format", PushCommits.Count);

            if (PushCommits.Count > 0)
            {
                PushSelectedCommit = PushCommits[0];
                await LoadPushCommitFilesAsync(PushSelectedCommit);
            }

            OnPropertyChanged(nameof(PushSingleCommitMode));
            OnPropertyChanged(nameof(PushBranchLineText));
            OnPropertyChanged(nameof(PushFileCount));
            OnPropertyChanged(nameof(PushCommitCount));
            OnPropertyChanged(nameof(PushHasCommits));
        }
        catch (Exception ex)
        {
            PushStatusMessage = LocalizedText.Format("git.vm.push_dialog_prepare_failed", ex.Message);
            Log($"PreparePushPreviewAsync 异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            PushIsLoading = false;
        }
    }

    /// <summary>Loads changed files for a single commit to display in the push dialog.</summary>
    private async Task LoadPushCommitFilesAsync(GitCommitDto? commit)
    {
        PushChangedFiles.Clear();
        if (commit is null || SelectedRepository is null) return;
        try
        {
            var detail = await client.GetCommitDetailAsync(SelectedRepository.Id, commit.Sha);
            foreach (var f in detail.ChangedFiles) PushChangedFiles.Add(f);
            OnPropertyChanged(nameof(PushFileCount));
        }
        catch (Exception ex)
        {
            Log($"LoadPushCommitFilesAsync 异常：{ex.Message}");
        }
    }

    /// <summary>Loads combined file changes for all ahead commits (when user clicks the branch line).</summary>
    private async Task LoadAllPushChangesAsync()
    {
        PushChangedFiles.Clear();
        if (SelectedRepository is null || PushCommits.Count == 0) return;

        var allPaths = new HashSet<string>();
        var firstCommitFiles = new List<GitFileChangeDto>();

        foreach (var commit in PushCommits)
        {
            try
            {
                var detail = await client.GetCommitDetailAsync(SelectedRepository.Id, commit.Sha);
                foreach (var f in detail.ChangedFiles)
                {
                    if (allPaths.Add(f.Path))
                        firstCommitFiles.Add(f);
                }
            }
            catch { /* skip failed commits */ }
        }

        foreach (var f in firstCommitFiles) PushChangedFiles.Add(f);
        OnPropertyChanged(nameof(PushFileCount));
    }

    /// <summary>Called when user selects a commit in the push dialog's left panel.</summary>
    [RelayCommand]
    private async Task SelectPushCommitAsync(GitCommitDto? commit)
    {
        if (commit is null) return;
        PushSelectedCommit = commit;
        OnPropertyChanged(nameof(PushSingleCommitMode));
        await LoadPushCommitFilesAsync(commit);
    }

    /// <summary>Called when user clicks the branch line to view all changes combined.</summary>
    [RelayCommand]
    private async Task SelectAllPushCommitsAsync()
    {
        PushSelectedCommit = null;
        OnPropertyChanged(nameof(PushSingleCommitMode));
        await LoadAllPushChangesAsync();
    }

    /// <summary>Opens the remote/branch picker dialog for the push target.</summary>
    [RelayCommand]
    private async Task SelectPushRemoteBranchAsync()
    {
        if (ShowRemoteBranchPickerDialogAsync is null) return;
        var result = await ShowRemoteBranchPickerDialogAsync(PushSelectedRemote, PushSelectedBranch);
        if (result.HasValue)
        {
            PushSelectedRemote = result.Value.Remote;
            PushSelectedBranch = result.Value.Branch;
            OnPropertyChanged(nameof(PushBranchLineText));
        }
    }

    /// <summary>Commits with the intention of pushing afterwards.
    /// Same as CommitAsync but auto-opens the push dialog on success.</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CommitAndPushAsync()
    {
        if (SelectedRepository is null) return;
        if (SelectedCount == 0)
        {
            await NotifyAsync(LocalizedText.Get("git.status.no_files_selected"));
            return;
        }
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            await NotifyAsync(LocalizedText.Get("git.status.commit_message_required"));
            return;
        }

        IsBusy = true;
        try
        {
            var request = new GitCommitRequest(CommitMessage, SelectedFilePaths.ToArray(), false);
            var result = await client.CommitAsync(SelectedRepository.Id, request);
            if (result.Success)
            {
                StatusText = LocalizedText.Get("git.status.committed");
                CommitMessage = string.Empty;
                await RefreshAllAsync();

                await PreparePushPreviewAsync();

                if (ShowPushDialogAsync is not null)
                {
                    var confirmed = await ShowPushDialogAsync();
                    if (confirmed)
                        await ExecutePushNowAsync();
                }
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.status.commit_failed_format", result.Message));
                await RefreshAllAsync();
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    /// <summary>Shows the push dialog for preview/confirmation after a regular commit.
    /// Called from CommitAsync after successful commit.</summary>
    public async Task ShowPushDialogAfterCommitAsync()
    {
        await PreparePushPreviewAsync();
        if (ShowPushDialogAsync is not null)
        {
            var confirmed = await ShowPushDialogAsync();
            if (confirmed)
                await ExecutePushNowAsync();
        }
    }

    /// <summary>Executes the actual push operation with current dialog settings.</summary>
    private async Task ExecutePushNowAsync()
    {
        if (SelectedRepository is null) return;
        IsBusy = true;
        StatusText = LocalizedText.Get("git.vm.push_progress");
        try
        {
            var result = await client.PushAsync(SelectedRepository.Id);
            if (result.RequiresCredentials)
                await NotifyAsync(LocalizedText.Get("git.vm.credentials_required"));
            else
            {
                if (result.Success)
                    StatusText = LocalizedText.Get("git.vm.pushed");
                else
                    await NotifyAsync(LocalizedText.Format("git.vm.push_failed_format", result.Message));
                await RefreshAllAsync();
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    /// <summary>获取远程分支名称列表（从已加载的 Branches 中过滤 IsRemote=true）。
    /// 如分支列表未加载则触发一次加载。</summary>
    public async Task<IReadOnlyList<string>> GetRemoteBranchNamesAsync()
    {
        if (SelectedRepository is null) return Array.Empty<string>();
        if (Branches.Count == 0)
        {
            try
            {
                var branches = await client.ListBranchesAsync(SelectedRepository.Id);
                Branches.Clear();
                foreach (var b in branches) Branches.Add(b);
            }
            catch { /* silent */ }
        }
        return Branches.Where(b => b.IsRemote).Select(b =>
        {
            // Strip remote prefix (e.g., "origin/master" → "master")
            var slashIdx = b.Name.IndexOf('/');
            return slashIdx >= 0 ? b.Name.Substring(slashIdx + 1) : b.Name;
        }).Distinct().ToList();
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
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.branch_created_format", request.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", ex.Message)); }
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
        if (branch.IsRemote) { await NotifyAsync(LocalizedText.Get("git.vm.rename_remote_branch")); return; }

        // 弹输入框取新名称；对话框未接入壳时走二次确认+占位提示（服务端接口已就绪）
        string? newName = null;
        if (ShowRenameBranchDialogAsync is not null)
            newName = await ShowRenameBranchDialogAsync(branch);
        else if (ShowConfirmAsync is not null)
        {
            // 壳暂未注入重命名输入对话框时，降级为直接尝试一个合理默认行为：追加 "-2" 后缀（便于先跑通接口链路）
            if (!await ShowConfirmAsync(LocalizedText.Format("git.vm.rename_confirm_format", branch.Name)))
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
                StatusText = LocalizedText.Format("git.vm.renamed_format", branch.Name, newName);
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.rename_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.rename_failed_format", ex.Message)); }
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
                && !await ShowConfirmAsync(LocalizedText.Format("git.vm.merge_confirm_format", branch.Name)))
                return;
        }

        IsBusy = true;
        try
        {
            var result = await client.MergeBranchAsync(SelectedRepository.Id, request);
            if (result.Conflicts is not null && result.Conflicts.Count > 0)
            {
                StatusText = LocalizedText.Format("git.vm.merge_conflicts_format", result.Conflicts.Count);
                HasConflicts = true;
                ActivePage = GitClientPage.ConflictResolution;
                await RefreshAllAsync();
            }
            else if (result.Success)
            {
                StatusText = LocalizedText.Format("git.vm.merged_format", request.Source);
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.merge_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.merge_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RebaseBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.rebase_unimplemented_format", branch.Name));
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null) return;
        if (branch.IsRemote) { await NotifyAsync(LocalizedText.Get("git.vm.cannot_push_remote")); return; }
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
        if (branch.IsRemote) { await NotifyAsync(LocalizedText.Get("git.vm.cannot_set_tracking_remote")); return; }

        GitBranchTrackingRequest? request;
        if (ShowSetTrackingDialogAsync is not null)
        {
            request = await ShowSetTrackingDialogAsync(branch);
            if (request is null) return;
        }
        else
        {
            // 默认：若分支名形式为 "origin/foo" 则尝试匹配远程分支；否则自动绑定 origin/<same-name>
            if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.set_tracking_confirm_format", branch.Name)))
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
                    ? LocalizedText.Format("git.vm.tracking_unset_format", branch.Name)
                    : LocalizedText.Format("git.vm.tracking_set_format", branch.Name,
                        string.IsNullOrWhiteSpace(request.Upstream) ? $"{request.Remote}/{request.Branch}" : request.Upstream);
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.set_tracking_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.set_tracking_failed_format", ex.Message)); }
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
                    StatusText = LocalizedText.Format("git.vm.copy_sha_format", commit.ShortSha);
                    return;
                }
            }
            // 兜底：通过 Dispatcher + TopLevel 遍历查找
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var tl = Avalonia.Controls.TopLevel.GetTopLevel((Avalonia.Visual?)null);
                if (tl?.Clipboard is not null) await tl.Clipboard.SetTextAsync(commit.Sha);
            });
            StatusText = LocalizedText.Format("git.vm.sha_no_toplevel_format", commit.ShortSha);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.copy_failed_format", ex.Message)); }
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
                    StatusText = LocalizedText.Format("git.vm.copied_short_sha_format", commit.ShortSha);
                    return;
                }
            }
            StatusText = LocalizedText.Format("git.vm.short_sha_format", commit.ShortSha);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.copy_failed_format", ex.Message)); }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null || SelectedRepository is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.checkout_commit_confirm_format", commit.ShortSha)))
            return;
        IsBusy = true;
        try
        {
            var result = await client.CheckoutAsync(SelectedRepository.Id, new GitCheckoutRequest(commit.Sha));
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.checked_out_format", commit.ShortSha);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.checkout_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.checkout_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ResetToCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        // TODO: 服务端尚未实现 git reset 端点（--soft/--mixed/--hard）
        await NotifyAsync(LocalizedText.Format("git.vm.reset_unimplemented_format", commit.ShortSha));
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
        await NotifyAsync(LocalizedText.Format("git.vm.undo_commit_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task CreatePatchFromCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.create_patch_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CherryPickCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.cherry_pick_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task RebaseInteractiveFromHereAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.rebase_interactive_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task SquashFromHereAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.squash_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task EditCommitMessageAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.edit_message_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushAllBeforeAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.push_before_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task CreateTagFromCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.create_tag_unimplemented_format", commit.ShortSha));
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
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.branch_created_at_format", request.Name, commit.ShortSha);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", ex.Message)); }
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
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.diff_failed_format_v2", ex.Message)); }
    }

    [RelayCommand]
    private async Task OpenFileInEditorAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        // TODO: 通过 RemoteOS 内置 CodeEditor 或宿主 OS 默认编辑器打开，当前占位
        await NotifyAsync(LocalizedText.Format("git.vm.open_file_unimplemented_format", file.Path));
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertFileChangeAsync(GitFileChangeDto? file)
    {
        file ??= SelectedFile;
        if (file is null || SelectedRepository is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.revert_file_confirm_format", file.Path)))
            return;
        // TODO: 服务端尚未实现 git checkout -- <path> / restore 端点
        await NotifyAsync(LocalizedText.Format("git.vm.revert_file_unimplemented_format", file.Path));
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
        await NotifyAsync(LocalizedText.Format("git.vm.unstage_unimplemented_format", file.Path));
    }

    [RelayCommand]
    private async Task ShowFileHistoryAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.file_history_unimplemented_format", file.Path));
    }

    [RelayCommand]
    private async Task CreatePatchFromFileAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.create_patch_file_unimplemented_format", file.Path));
    }
}
