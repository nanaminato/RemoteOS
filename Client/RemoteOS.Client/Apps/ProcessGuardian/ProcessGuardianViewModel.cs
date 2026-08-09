using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.ProcessGuardian;

namespace Client.Apps.ProcessGuardian;

public sealed partial class ProcessGuardianViewModel(IProcessGuardianClient client) : ObservableObject
{
    public ObservableCollection<GuardianWorkloadDto> Workloads { get; } = [];
    public ObservableCollection<GuardianLogEntryDto> Logs { get; } = [];
    [ObservableProperty] private GuardianWorkloadDto? _selectedWorkload;
    [ObservableProperty] private string _statusText = LocalizedText.Get("guardian.status.loading");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _definitionId = string.Empty;
    [ObservableProperty] private string _definitionName = string.Empty;
    [ObservableProperty] private string _executablePath = string.Empty;
    [ObservableProperty] private string _workingDirectory = string.Empty;
    [ObservableProperty] private string _argumentsText = string.Empty;
    [ObservableProperty] private bool _enabledOnBoot;

    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return; IsLoading = true;
        try
        {
            var statusTask = client.GetStatusAsync(); var workloadsTask = client.ListWorkloadsAsync();
            await Task.WhenAll(statusTask, workloadsTask); var status = await statusTask;
            StatusText = status.IsInstalled
                ? LocalizedText.Format("guardian.status.available", status.Version ?? "")
                : status.ProblemCode is "guardian.agent_not_configured" or "guardian.agent_not_installed"
                    ? LocalizedText.Get("guardian.status.install_required")
                    : LocalizedText.Format("guardian.status.unavailable", status.ProblemCode);
            Workloads.Clear(); foreach (var workload in await workloadsTask) Workloads.Add(workload);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.status.failed", exception.Message); }
        finally { IsLoading = false; }
    }
    [RelayCommand(CanExecute = nameof(HasSelectedWorkload))] private Task StartWorkloadAsync() => ApplyActionAsync("start");
    [RelayCommand(CanExecute = nameof(HasSelectedWorkload))] private Task StopWorkloadAsync() => ApplyActionAsync("stop");
    [RelayCommand(CanExecute = nameof(HasSelectedWorkload))] private Task RestartWorkloadAsync() => ApplyActionAsync("restart");
    [RelayCommand(CanExecute = nameof(HasSelectedWorkload))]
    private async Task DeleteWorkloadAsync()
    {
        var workload = SelectedWorkload; if (workload is null) return; IsLoading = true;
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
    [RelayCommand(CanExecute = nameof(HasSelectedWorkload))] private async Task LoadLogsAsync()
    {
        var workload = SelectedWorkload; if (workload is null) return; IsLoading = true;
        try { var logs = await client.ListLogsAsync(workload.Id); Logs.Clear(); foreach (var log in logs) Logs.Add(log); StatusText = LocalizedText.Format("guardian.logs.loaded", workload.Name); }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.logs.failed", exception.Message); }
        finally { IsLoading = false; }
    }
    private bool HasSelectedWorkload => SelectedWorkload is not null && !IsLoading;
    partial void OnSelectedWorkloadChanged(GuardianWorkloadDto? value)
    {
        NotifyActionCommands();
        if (value is not null) _ = LoadDefinitionAsync(value.Id);
    }
    partial void OnIsLoadingChanged(bool value) => NotifyActionCommands();
    private void NotifyActionCommands() { StartWorkloadCommand.NotifyCanExecuteChanged(); StopWorkloadCommand.NotifyCanExecuteChanged(); RestartWorkloadCommand.NotifyCanExecuteChanged(); DeleteWorkloadCommand.NotifyCanExecuteChanged(); LoadLogsCommand.NotifyCanExecuteChanged(); }
    private async Task ApplyActionAsync(string action)
    {
        var workload = SelectedWorkload; if (workload is null) return; IsLoading = true;
        try { var result = await client.ApplyActionAsync(workload.Id, action); StatusText = result.Success ? LocalizedText.Format("guardian.action.succeeded", action, workload.Name) : LocalizedText.Format("guardian.action.failed", action, result.ProblemCode); }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.action.failed", action, exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CreateWorkloadAsync()
    {
        if (string.IsNullOrWhiteSpace(DefinitionId) || string.IsNullOrWhiteSpace(DefinitionName) || string.IsNullOrWhiteSpace(ExecutablePath) || string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            StatusText = LocalizedText.Get("guardian.validation.required");
            return;
        }
        IsLoading = true;
        try
        {
            var arguments = ArgumentsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var definition = new ProcessDefinitionDto(DefinitionId.Trim(), DefinitionName.Trim(), ExecutablePath.Trim(), arguments, WorkingDirectory.Trim(), EnabledOnBoot);
            var result = await client.UpsertAsync(definition);
            StatusText = result.Success ? LocalizedText.Format("guardian.create.succeeded", DefinitionName) : LocalizedText.Format("guardian.create.failed", result.ProblemCode);
            if (result.Success) ClearDefinition();
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.create.failed", exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
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
        DefinitionId = DefinitionName = ExecutablePath = WorkingDirectory = ArgumentsText = string.Empty;
        EnabledOnBoot = false;
        SelectedWorkload = null;
    }
}
