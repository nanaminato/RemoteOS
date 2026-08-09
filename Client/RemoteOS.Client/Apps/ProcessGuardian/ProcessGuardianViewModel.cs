using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.ProcessGuardian;

namespace Client.Apps.ProcessGuardian;

public sealed partial class ProcessGuardianViewModel(IProcessGuardianClient client) : ObservableObject
{
    private int _latestRefreshVersion;
    private int _activeRefreshes;
    public ObservableCollection<GuardianWorkloadDto> Workloads { get; } = [];
    [ObservableProperty] private GuardianWorkloadDto? _selectedWorkload;
    [ObservableProperty] private string _statusText = LocalizedText.Get("guardian.status.loading");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _definitionId = string.Empty;
    [ObservableProperty] private string _definitionName = string.Empty;
    [ObservableProperty] private string _executablePath = string.Empty;
    [ObservableProperty] private string _workingDirectory = string.Empty;
    [ObservableProperty] private string _argumentsText = string.Empty;
    [ObservableProperty] private bool _enabledOnBoot;

    public Func<bool, Task>? ShowEditorAsync { get; set; }
    public Func<GuardianWorkloadDto, Task>? ShowLogsAsync { get; set; }
    public Func<Task>? CloseEditorAsync { get; set; }

    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand]
    private async Task RefreshAsync()
    {
        var refreshVersion = Interlocked.Increment(ref _latestRefreshVersion);
        Interlocked.Increment(ref _activeRefreshes);
        IsLoading = true;
        try
        {
            var statusTask = client.GetStatusAsync(); var workloadsTask = client.ListWorkloadsAsync();
            await Task.WhenAll(statusTask, workloadsTask); var status = await statusTask;
            if (refreshVersion != Volatile.Read(ref _latestRefreshVersion)) return;
            StatusText = status.IsInstalled
                ? LocalizedText.Format("guardian.status.available", status.Version ?? "")
                : status.ProblemCode is "guardian.agent_not_configured" or "guardian.agent_not_installed"
                    ? LocalizedText.Get("guardian.status.install_required")
                    : LocalizedText.Format("guardian.status.unavailable", status.ProblemCode);
            Workloads.Clear(); foreach (var workload in await workloadsTask) Workloads.Add(workload);
        }
        catch (Exception exception)
        {
            if (refreshVersion == Volatile.Read(ref _latestRefreshVersion))
                StatusText = LocalizedText.Format("guardian.status.failed", exception.Message);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeRefreshes) == 0)
                IsLoading = false;
        }
    }
    [RelayCommand] private Task OpenCreateWorkloadAsync()
    {
        ClearDefinition();
        return ShowEditorAsync?.Invoke(false) ?? Task.CompletedTask;
    }
    [RelayCommand] private async Task EditWorkloadAsync(GuardianWorkloadDto? workload)
    {
        workload ??= SelectedWorkload;
        if (workload is null) return;
        await LoadDefinitionAsync(workload.Id);
        await (ShowEditorAsync?.Invoke(true) ?? Task.CompletedTask);
    }
    [RelayCommand] private Task StartWorkloadAsync(GuardianWorkloadDto? workload) => ApplyActionAsync("start", workload ?? SelectedWorkload);
    [RelayCommand] private Task StopWorkloadAsync(GuardianWorkloadDto? workload) => ApplyActionAsync("stop", workload ?? SelectedWorkload);
    [RelayCommand] private Task RestartWorkloadAsync(GuardianWorkloadDto? workload) => ApplyActionAsync("restart", workload ?? SelectedWorkload);
    [RelayCommand]
    private async Task DeleteWorkloadAsync(GuardianWorkloadDto? workload)
    {
        workload ??= SelectedWorkload; if (workload is null) return; IsLoading = true;
        try
        {
            var result = await client.DeleteAsync(workload.Id);
            StatusText = result.Success ? LocalizedText.Format("guardian.delete.succeeded", workload.Name) : LocalizedText.Format("guardian.delete.failed", result.ProblemCode);
            if (result.Success) ClearDefinition();
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.delete.failed", exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }
    [RelayCommand] private async Task OpenLogsAsync(GuardianWorkloadDto? workload)
    {
        workload ??= SelectedWorkload;
        if (workload is not null && ShowLogsAsync is not null) await ShowLogsAsync(workload);
    }
    private bool HasSelectedWorkload => SelectedWorkload is not null && !IsLoading;
    partial void OnSelectedWorkloadChanged(GuardianWorkloadDto? value)
    {
        NotifyActionCommands();
        if (value is not null) _ = LoadDefinitionAsync(value.Id);
    }
    partial void OnIsLoadingChanged(bool value) => NotifyActionCommands();
    private void NotifyActionCommands() { }
    private async Task ApplyActionAsync(string action, GuardianWorkloadDto? workload)
    {
        if (workload is null) return; IsLoading = true;
        try { var result = await client.ApplyActionAsync(workload.Id, action); StatusText = result.Success ? LocalizedText.Format("guardian.action.succeeded", action, workload.Name) : LocalizedText.Format("guardian.action.failed", action, result.ProblemCode); }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.action.failed", action, exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CreateWorkloadAsync()
    {
        if (string.IsNullOrWhiteSpace(DefinitionName) || string.IsNullOrWhiteSpace(ExecutablePath) || string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            StatusText = LocalizedText.Get("guardian.validation.required");
            return;
        }
        var saved = false;
        IsLoading = true;
        try
        {
            var arguments = ArgumentsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var definition = new ProcessDefinitionDto(DefinitionId.Trim(), DefinitionName.Trim(), ExecutablePath.Trim(), arguments, WorkingDirectory.Trim(), EnabledOnBoot);
            var result = await client.UpsertAsync(definition);
            StatusText = result.Success ? LocalizedText.Format("guardian.create.succeeded", DefinitionName) : LocalizedText.Format("guardian.create.failed", result.ProblemCode);
            saved = result.Success;
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.create.failed", exception.Message); }
        finally { IsLoading = false; }
        if (!saved) return;
        await RefreshAsync();
        if (CloseEditorAsync is not null) await CloseEditorAsync();
    }

    private async Task LoadDefinitionAsync(string workloadId)
    {
        try
        {
            var result = await client.GetDefinitionAsync(workloadId);
            if (!result.Success || result.Definition is null || SelectedWorkload?.Id != workloadId) return;
            var definition = result.Definition;
            DefinitionId = definition.Id;
            DefinitionName = definition.Name;
            ExecutablePath = definition.ExecutablePath;
            WorkingDirectory = definition.WorkingDirectory;
            ArgumentsText = string.Join(Environment.NewLine, definition.Arguments);
            EnabledOnBoot = definition.EnabledOnBoot;
        }
        catch
        {
            // Refresh and actions remain available even when an optional definition read fails.
        }
    }

    private void ClearDefinition()
    {
        // The ID is the immutable key used by routes, persistence, and audit records.
        // It is deliberately generated by the client and never exposed for editing.
        DefinitionId = Guid.NewGuid().ToString("N");
        DefinitionName = ExecutablePath = WorkingDirectory = ArgumentsText = string.Empty;
        EnabledOnBoot = false;
        SelectedWorkload = null;
    }
}
