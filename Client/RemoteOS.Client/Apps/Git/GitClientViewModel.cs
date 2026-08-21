using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Git;

namespace Client.Apps.Git;

public enum GitClientPage { Overview, Workspace, Branches, History, ConflictResolution, Remotes }

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

    public bool HasUpstream => Status?.Upstream is not null;
    public bool CanManage => SelectedRepository is not null && !IsBusy;
    public bool CanOpenProject => !IsBusy && IsPickerMode;

    public async Task StartAsync()
    {
        await RefreshEngineStatusAsync();
        if (!IsGitAvailable)
        {
            IsGitInstallRequired = IsInstallRequired(IsGitAvailable, ProblemCode);
            StatusText = "Git 未安装或不可用";
            if (ShowGitUnavailableAsync is not null)
                await ShowGitUnavailableAsync();
            if (!IsGitAvailable) return; // still unavailable after dialog → stop further init
        }

        await RefreshRepositoriesAsync();
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
        try
        {
            var repos = await client.ListRepositoriesAsync();
            Repositories.Clear();
            foreach (var repo in repos) Repositories.Add(repo);
        }
        catch (Exception ex)
        {
            StatusText = $"加载项目列表失败：{ex.Message}";
        }
    }

    private async Task RefreshAllAsync()
    {
        if (SelectedRepository is null) return;
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try
        {
            var statusTask = client.GetStatusAsync(SelectedRepository.Id);
            var branchesTask = client.ListBranchesAsync(SelectedRepository.Id);
            var logTask = client.GetLogAsync(SelectedRepository.Id, limit: 100);

            Status = await statusTask;
            var branches = await branchesTask;
            var commits = await logTask;

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
            }

            StatusText = $"Ready — {Status?.Branch ?? "unknown"}";
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
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
        if (repo is null) return;
        IsPickerMode = false;
        SelectedRepository = repo;
        ActivePage = GitClientPage.Overview;
        StatusText = $"Loading {repo.Name}…";
        await RefreshAllAsync();
        StartStatusTimer();
        await RefreshRemotesAsync();
    }

    /// <summary>打开远程文件夹选择器；选中后探测 Git 状态：是仓库则注册并打开；否则提示初始化。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenFolderAsync()
    {
        if (ShowRemotePathPickerAsync is null)
        {
            StatusText = "路径选择器不可用";
            return;
        }

        var path = await ShowRemotePathPickerAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        await ProbeAndOpenAsync(path);
    }

    /// <summary>手动注册一个绝对路径作为 Git 项目（输入对话框形式）。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task RegisterRepositoryAsync()
    {
        if (ShowRegisterRepositoryDialogAsync is null) return;
        var registration = await ShowRegisterRepositoryDialogAsync();
        if (registration is null) return;
        try
        {
            var dto = await client.RegisterRepositoryAsync(registration);
            if (!Repositories.Contains(dto))
                Repositories.Add(dto);
            StatusText = $"已注册项目「{dto.Name}」，正在打开…";
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex) { StatusText = $"注册失败：{ex.Message}"; }
    }

    private async Task ProbeAndOpenAsync(string path)
    {
        IsProbing = true;
        IsBusy = true;
        ProbeHint = $"正在检查 {path} …";
        try
        {
            var probe = await client.ProbeRepositoryAsync(path);
            if (!probe.IsRepository)
            {
                ProbeHint = $"所选目录不是 Git 仓库";
                var init = ShowInitConfirmAsync is not null && await ShowInitConfirmAsync(path);
                if (!init)
                {
                    StatusText = "已取消初始化";
                    return;
                }
                var initResult = await client.InitRepositoryAsync(path);
                if (!initResult.Success)
                {
                    StatusText = $"git init 失败：{initResult.Message}";
                    return;
                }
                StatusText = "Git 仓库已初始化";
                // 重新探测以获取最新状态（如 defaultBranch）
                probe = await client.ProbeRepositoryAsync(path);
            }

            // 已是 Git 仓库：检查是否已注册
            var existing = Repositories.FirstOrDefault(r =>
                string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                StatusText = $"项目已存在：{existing.Name}";
                await OpenProjectCommand.ExecuteAsync(existing);
                return;
            }

            // 未注册：自动注册（名称取路径末段）
            var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = path;
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
        }
        finally
        {
            IsProbing = false;
            IsBusy = false;
        }
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
        if (ShowCommitDialogAsync is null || SelectedRepository is null) return;
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
        if (SelectedRepository is null) return;
        try
        {
            var remotes = await client.ListRemotesAsync(SelectedRepository.Id);
            Remotes.Clear();
            foreach (var r in remotes) Remotes.Add(r);
            if (Remotes.Count > 0) SelectedRemote = Remotes[0];
            else SelectedRemote = null;
        }
        catch (Exception ex) { StatusText = $"加载远程列表失败：{ex.Message}"; }
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
}
