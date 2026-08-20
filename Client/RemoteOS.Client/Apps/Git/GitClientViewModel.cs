using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Git;

namespace Client.Apps.Git;

public enum GitClientPage { Overview, Workspace, Branches, History, ConflictResolution }

/// <summary>State and typed operations for the Git Client. Uses DispatcherTimer (10s) for status refresh with Interlocked reentrancy guard.</summary>
public sealed partial class GitClientViewModel(IRemoteGitClient client) : ObservableObject
{
    public ObservableCollection<GitRepositoryDto> Repositories { get; } = [];
    public ObservableCollection<GitBranchDto> Branches { get; } = [];
    public ObservableCollection<GitCommitDto> Commits { get; } = [];
    public ObservableCollection<GitFileChangeDto> StagedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> UnstagedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> UntrackedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> ConflictFiles { get; } = [];

    [ObservableProperty] private GitRepositoryDto? _selectedRepository;
    [ObservableProperty] private GitClientPage _activePage = GitClientPage.Overview;
    [ObservableProperty] private GitStatusDto? _status;
    [ObservableProperty] private GitBranchDto? _selectedBranch;
    [ObservableProperty] private GitCommitDto? _selectedCommit;
    [ObservableProperty] private GitFileChangeDto? _selectedFile;
    [ObservableProperty] private GitDiffDto? _fileDiff;
    [ObservableProperty] private string _statusText = "Loading…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAutoRefresh = true;
    [ObservableProperty] private string _commitMessage = string.Empty;
    [ObservableProperty] private bool _hasConflicts;

    private DispatcherTimer? _timer;
    private int _refreshing;

    /// <summary>Dialog callbacks assigned by the app shell.</summary>
    public Func<Task<GitCommitRequest?>>? ShowCommitDialogAsync { get; set; }
    public Func<Task<GitBranchCreateRequest?>>? ShowCreateBranchDialogAsync { get; set; }
    public Func<Task<GitPullRequest?>>? ShowPullDialogAsync { get; set; }
    public Func<Task<GitRepositoryRegistration?>>? ShowRegisterRepositoryDialogAsync { get; set; }
    public Func<string, Task<bool>>? ShowConfirmAsync { get; set; }

    public bool HasUpstream => Status?.Upstream is not null;
    public bool CanManage => SelectedRepository is not null && !IsBusy;

    public async Task StartAsync()
    {
        try
        {
            var repos = await client.ListRepositoriesAsync();
            Repositories.Clear();
            foreach (var repo in repos) Repositories.Add(repo);
            SelectedRepository = Repositories.FirstOrDefault();
            if (SelectedRepository is not null)
                await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load repositories: {ex.Message}";
        }

        if (IsAutoRefresh)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _timer.Tick += async (_, _) => await RefreshStatusAsync();
            _timer.Start();
        }
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
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

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RefreshAsync()
    {
        await RefreshAllAsync();
    }

    [RelayCommand]
    private async Task SwitchRepositoryAsync()
    {
        if (SelectedRepository is null) return;
        await RefreshAllAsync();
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
    private async Task RegisterRepositoryAsync()
    {
        if (ShowRegisterRepositoryDialogAsync is null) return;
        var registration = await ShowRegisterRepositoryDialogAsync();
        if (registration is null) return;
        try
        {
            var dto = await client.RegisterRepositoryAsync(registration);
            Repositories.Add(dto);
            SelectedRepository = dto;
            StatusText = $"Repository '{dto.Name}' registered";
            await RefreshAllAsync();
        }
        catch (Exception ex) { StatusText = $"Registration failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void NavigateTo(GitClientPage page) => ActivePage = page;
}
