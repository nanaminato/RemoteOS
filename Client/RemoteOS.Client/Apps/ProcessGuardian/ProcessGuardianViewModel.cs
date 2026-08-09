using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.ProcessGuardian;

namespace Client.Apps.ProcessGuardian;

public sealed partial class ProcessGuardianViewModel(IProcessGuardianClient client) : ObservableObject
{
    public ObservableCollection<GuardianWorkloadDto> Workloads { get; } = [];
    [ObservableProperty] private GuardianWorkloadDto? _selectedWorkload;
    [ObservableProperty] private string _statusText = LocalizedText.Get("guardian.status.loading");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _definitionId = string.Empty;
    [ObservableProperty] private string _definitionName = string.Empty;
    [ObservableProperty] private string _executablePath = string.Empty;
    [ObservableProperty] private string _workingDirectory = string.Empty;
    [ObservableProperty] private string _argumentsText = string.Empty;

    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return; IsLoading = true;
        try
        {
            var statusTask = client.GetStatusAsync(); var workloadsTask = client.ListWorkloadsAsync();
            await Task.WhenAll(statusTask, workloadsTask); var status = await statusTask;
            StatusText = status.IsInstalled ? LocalizedText.Format("guardian.status.available", status.Version ?? "") : LocalizedText.Format("guardian.status.unavailable", status.ProblemCode);
            Workloads.Clear(); foreach (var workload in await workloadsTask) Workloads.Add(workload);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.status.failed", exception.Message); }
        finally { IsLoading = false; }
    }
    [RelayCommand(CanExecute = nameof(HasSelectedWorkload))] private Task StartWorkloadAsync() => ApplyActionAsync("start");
    [RelayCommand(CanExecute = nameof(HasSelectedWorkload))] private Task StopWorkloadAsync() => ApplyActionAsync("stop");
    [RelayCommand(CanExecute = nameof(HasSelectedWorkload))] private Task RestartWorkloadAsync() => ApplyActionAsync("restart");
    private bool HasSelectedWorkload => SelectedWorkload is not null && !IsLoading;
    partial void OnSelectedWorkloadChanged(GuardianWorkloadDto? value) => NotifyActionCommands();
    partial void OnIsLoadingChanged(bool value) => NotifyActionCommands();
    private void NotifyActionCommands() { StartWorkloadCommand.NotifyCanExecuteChanged(); StopWorkloadCommand.NotifyCanExecuteChanged(); RestartWorkloadCommand.NotifyCanExecuteChanged(); }
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
            var definition = new ProcessDefinitionDto(DefinitionId.Trim(), DefinitionName.Trim(), ExecutablePath.Trim(), arguments, WorkingDirectory.Trim());
            var result = await client.UpsertAsync(definition);
            StatusText = result.Success ? LocalizedText.Format("guardian.create.succeeded", DefinitionName) : LocalizedText.Format("guardian.create.failed", result.ProblemCode);
            if (result.Success) { DefinitionId = DefinitionName = ExecutablePath = WorkingDirectory = ArgumentsText = string.Empty; }
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("guardian.create.failed", exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }
}
