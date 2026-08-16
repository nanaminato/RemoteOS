using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Docker;

namespace Client.Apps.Docker;

/// <summary>State and safe, typed operations for the server-local Docker Manager.</summary>
public sealed partial class DockerManagerViewModel(IRemoteDockerClient client) : ObservableObject
{
    public ObservableCollection<DockerContainerDto> Containers { get; } = [];
    public ObservableCollection<DockerImageDto> Images { get; } = [];
    public ObservableCollection<DockerNetworkDto> Networks { get; } = [];
    public ObservableCollection<DockerVolumeDto> Volumes { get; } = [];
    public ObservableCollection<DockerStackDto> Stacks { get; } = [];
    public ObservableCollection<string> AvailableNetworks { get; } = ["bridge"];

    // Docker's built-in drivers that can create a user-defined network. Host and none are
    // built-in special networks, rather than choices for `docker network create`.
    public IReadOnlyList<string> NetworkDrivers { get; } = ["bridge", "ipvlan", "macvlan", "overlay"];
    public IReadOnlyList<string> VolumeDrivers { get; } = ["local"];
    public IReadOnlyList<string> RestartPolicies { get; } = ["no", "always", "unless-stopped", "on-failure"];

    [ObservableProperty] private string _statusText = LocalizedText.Get("docker.status.loading");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty] private string _operationTitle = string.Empty;
    [ObservableProperty] private string _operationLog = string.Empty;
    [ObservableProperty] private bool _isDockerAvailable;
    [ObservableProperty] private bool _isDockerInstallRequired;
    [ObservableProperty] private string _engineVersion = "—";
    [ObservableProperty] private string _enginePlatform = "—";
    [ObservableProperty] private DockerContainerDto? _selectedContainer;
    [ObservableProperty] private string _containerLogs = string.Empty;
    [ObservableProperty] private string _containerStats = string.Empty;
    [ObservableProperty] private bool _confirmContainerDeletion;
    [ObservableProperty] private string _stackName = string.Empty;
    [ObservableProperty] private string _composeYaml = string.Empty;
    [ObservableProperty] private string _imageReference = string.Empty;
    [ObservableProperty] private string _containerName = string.Empty;
    [ObservableProperty] private string _containerImage = string.Empty;
    [ObservableProperty] private string _containerArguments = string.Empty;
    [ObservableProperty] private string _containerPorts = string.Empty;
    [ObservableProperty] private string _containerEnvironment = string.Empty;
    [ObservableProperty] private string _containerMounts = string.Empty;
    [ObservableProperty] private string _containerNetwork = "bridge";
    [ObservableProperty] private string _containerRestartPolicy = "unless-stopped";
    [ObservableProperty] private DockerImageDto? _selectedImage;
    [ObservableProperty] private bool _confirmImageDeletion;
    [ObservableProperty] private string _networkName = string.Empty;
    [ObservableProperty] private string _selectedNetworkDriver = "bridge";
    [ObservableProperty] private DockerNetworkDto? _selectedNetwork;
    [ObservableProperty] private bool _confirmNetworkDeletion;
    [ObservableProperty] private string _volumeName = string.Empty;
    [ObservableProperty] private string _selectedVolumeDriver = "local";
    [ObservableProperty] private DockerVolumeDto? _selectedVolume;
    [ObservableProperty] private bool _confirmVolumeDeletion;

    /// <summary>Assigned by the app shell so operations can surface an unavailable Engine immediately.</summary>
    public Func<Task>? ShowDockerUnavailableAsync { get; set; }
    /// <summary>Assigned by the app shell to open the localized Docker installation guide.</summary>
    public Func<Task>? OpenDockerInstallGuideAsync { get; set; }
    private bool _isUnavailableDialogShowing;

    public int RunningContainerCount => Containers.Count(container => container.State.Equals("running", StringComparison.OrdinalIgnoreCase));
    public bool HasOperationActivity => !string.IsNullOrWhiteSpace(OperationTitle);

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var statusTask = client.GetStatusAsync();
            var containersTask = client.ListContainersAsync();
            var imagesTask = client.ListImagesAsync();
            var networksTask = client.ListNetworksAsync();
            var volumesTask = client.ListVolumesAsync();
            var stacksTask = client.ListStacksAsync();
            await Task.WhenAll(statusTask, containersTask, imagesTask, networksTask, volumesTask, stacksTask);
            var status = await statusTask;
            IsDockerAvailable = status.IsAvailable;
            IsDockerInstallRequired = IsInstallRequired(status.IsAvailable, status.ProblemCode);
            EngineVersion = status.ServerVersion ?? "—";
            EnginePlatform = string.Join(" / ", new[] { status.OperatingSystem, status.Architecture }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(EnginePlatform)) EnginePlatform = "—";
            StatusText = status.IsAvailable
                ? LocalizedText.Format("docker.status.available", status.ServerVersion ?? "", status.OperatingSystem ?? "")
                : LocalizedText.Format("docker.status.unavailable", status.ProblemCode);
            Replace(Containers, await containersTask); Replace(Images, await imagesTask);
            var networks = await networksTask;
            Replace(Networks, networks); Replace(Volumes, await volumesTask);
            Replace(Stacks, await stacksTask);
            Replace(AvailableNetworks, networks.Select(network => network.Name).Prepend("bridge").Distinct(StringComparer.Ordinal));
            if (!AvailableNetworks.Contains(ContainerNetwork, StringComparer.Ordinal)) ContainerNetwork = "bridge";
            OnPropertyChanged(nameof(RunningContainerCount));
        }
        catch (Exception exception)
        {
            IsDockerInstallRequired = false;
            StatusText = LocalizedText.Format("docker.status.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear(); foreach (var value in values) target.Add(value);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task StartContainerAsync() => ApplyContainerActionAsync("start");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task StopContainerAsync() => ApplyContainerActionAsync("stop");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task RestartContainerAsync() => ApplyContainerActionAsync("restart");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task PauseContainerAsync() => ApplyContainerActionAsync("pause");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task UnpauseContainerAsync() => ApplyContainerActionAsync("unpause");
    [RelayCommand(CanExecute = nameof(CanDeleteContainer))] private Task DeleteContainerAsync() => ApplyContainerActionAsync("delete", true);
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private async Task LoadContainerLogsAsync()
    {
        var container = SelectedContainer; if (container is null) return;
        await RunReadAsync(async () =>
        {
            var logs = await client.GetContainerLogsAsync(container.Id);
            ContainerLogs = logs is null ? string.Empty : string.Join(Environment.NewLine, logs.Lines);
            AppendOperationLog(logs?.Lines);
            StatusText = logs is null ? LocalizedText.Format("docker.action.failed", OperationText("logs"), "docker.not_found") : LocalizedText.Format("docker.action.succeeded", OperationText("logs"), container.Names);
        });
    }
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private async Task LoadContainerStatsAsync()
    {
        var container = SelectedContainer; if (container is null) return;
        await RunReadAsync(async () =>
        {
            var stats = await client.GetContainerStatsAsync(container.Id);
            ContainerStats = stats is null ? string.Empty : LocalizedText.Format("docker.stats.summary", stats.CpuPercent, stats.MemoryUsage, stats.NetworkIo, stats.BlockIo);
            StatusText = stats is null ? LocalizedText.Format("docker.action.failed", OperationText("stats"), "docker.not_found") : LocalizedText.Format("docker.action.succeeded", OperationText("stats"), container.Names);
        });
    }

    private bool HasSelectedContainer => SelectedContainer is not null && !IsLoading;
    private bool CanDeleteContainer => HasSelectedContainer && ConfirmContainerDeletion;
    partial void OnSelectedContainerChanged(DockerContainerDto? value)
    {
        ContainerLogs = ContainerStats = string.Empty;
        NotifyContainerCommands();
    }
    partial void OnConfirmContainerDeletionChanged(bool value) => DeleteContainerCommand.NotifyCanExecuteChanged();
    partial void OnIsLoadingChanged(bool value)
    {
        NotifyContainerCommands();
        DeleteImageCommand.NotifyCanExecuteChanged(); DeleteNetworkCommand.NotifyCanExecuteChanged(); DeleteVolumeCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsDockerInstallRequiredChanged(bool value) => OpenInstallGuideCommand.NotifyCanExecuteChanged();

    private bool CanOpenInstallGuide => IsDockerInstallRequired && OpenDockerInstallGuideAsync is not null;

    [RelayCommand(CanExecute = nameof(CanOpenInstallGuide))]
    private async Task OpenInstallGuideAsync()
    {
        if (OpenDockerInstallGuideAsync is not null)
            await OpenDockerInstallGuideAsync();
    }
    private void NotifyContainerCommands()
    {
        StartContainerCommand.NotifyCanExecuteChanged(); StopContainerCommand.NotifyCanExecuteChanged(); RestartContainerCommand.NotifyCanExecuteChanged();
        PauseContainerCommand.NotifyCanExecuteChanged(); UnpauseContainerCommand.NotifyCanExecuteChanged(); DeleteContainerCommand.NotifyCanExecuteChanged();
        LoadContainerLogsCommand.NotifyCanExecuteChanged(); LoadContainerStatsCommand.NotifyCanExecuteChanged();
    }
    private async Task ApplyContainerActionAsync(string action, bool confirmed = false)
    {
        var container = SelectedContainer; if (container is null) return;
        await RunOperationAsync(
            () => client.ApplyContainerActionAsync(container.Id, action, new DockerContainerActionRequest(Confirmed: confirmed)),
            result => result.Success ? LocalizedText.Format("docker.action.succeeded", OperationText(action), container.Names) : LocalizedText.Format("docker.action.failed", OperationText(action), ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.container_action", OperationText(action), container.Names));
    }

    [RelayCommand] private Task ValidateStackAsync() => ApplyStackAsync("validate");
    [RelayCommand] private Task DeployStackAsync() => TryDeployStackAsync();

    /// <summary>Deploys a Compose stack and reports whether its dialog can close.</summary>
    public Task<bool> TryDeployStackAsync() => ApplyStackAsync("deploy");

    private async Task<bool> ApplyStackAsync(string operation)
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(StackName) || string.IsNullOrWhiteSpace(ComposeYaml)) { StatusText = LocalizedText.Get("docker.stack.required"); return false; }
        return await RunStackOperationAsync(operation);
    }
    private async Task<bool> RunStackOperationAsync(string operation)
    {
        return await RunOperationAsync(
            () => client.ApplyStackOperationAsync(operation, new DockerStackDefinitionDto(StackName.Trim(), ComposeYaml)),
            result =>
            {
                var detail = result.Messages.FirstOrDefault() ?? result.ProblemCode;
                return result.Success ? LocalizedText.Format("docker.stack.succeeded", OperationText(operation), StackName) : LocalizedText.Format("docker.stack.failed", OperationText(operation), detail);
            }, LocalizedText.Format("docker.operation.stack", OperationText(operation), StackName));
    }

    [RelayCommand] private Task PullImageAsync() => TryPullImageAsync();

    /// <summary>Pulls an image and reports whether the dialog can close.</summary>
    public async Task<bool> TryPullImageAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(ImageReference)) { StatusText = LocalizedText.Get("docker.image.required"); return false; }
        var imageReference = ImageReference.Trim();
        return await RunOperationAsync(
            () => client.PullImageAsync(new DockerImageOperationRequest(imageReference)),
            result => result.Success ? LocalizedText.Format("docker.image.pull_succeeded", imageReference) : LocalizedText.Format("docker.image.pull_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => ImageReference = string.Empty,
            operationName: LocalizedText.Format("docker.operation.pull", imageReference));
    }

    [RelayCommand] private Task CreateContainerAsync() => TryCreateContainerAsync();

    /// <summary>Creates a container and reports whether the dialog can close.</summary>
    public async Task<bool> TryCreateContainerAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ContainerImage)) { StatusText = LocalizedText.Get("docker.container.required"); return false; }
        var name = ContainerName.Trim();
        return await RunOperationAsync(
            () => client.CreateContainerAsync(new DockerContainerCreateRequest(
                name, ContainerImage.Trim(), Lines(ContainerArguments), Lines(ContainerPorts), Lines(ContainerEnvironment), Lines(ContainerMounts), ContainerNetwork, ContainerRestartPolicy)),
            result => result.Success ? LocalizedText.Format("docker.container.created", name) : LocalizedText.Format("docker.container.create_failed", ProblemText(result.ProblemCode)),
            onSuccess: () =>
            {
                ContainerName = ContainerImage = ContainerArguments = ContainerPorts = ContainerEnvironment = ContainerMounts = string.Empty;
                ContainerNetwork = "bridge"; ContainerRestartPolicy = "unless-stopped";
            },
            operationName: LocalizedText.Format("docker.operation.create_container", name));
    }

    [RelayCommand(CanExecute = nameof(CanDeleteImage))] private async Task DeleteImageAsync()
    {
        var image = SelectedImage; if (image is null) return;
        await RunOperationAsync(
            () => client.DeleteImageAsync(image.Id, new DockerImageOperationRequest(image.Id, true)),
            result => result.Success ? LocalizedText.Format("docker.image.deleted", image.Repository) : LocalizedText.Format("docker.image.delete_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.delete_image", image.Repository));
    }
    private bool CanDeleteImage => SelectedImage is not null && ConfirmImageDeletion && !IsLoading;
    partial void OnSelectedImageChanged(DockerImageDto? value) => DeleteImageCommand.NotifyCanExecuteChanged();
    partial void OnConfirmImageDeletionChanged(bool value) => DeleteImageCommand.NotifyCanExecuteChanged();

    [RelayCommand] private Task CreateNetworkAsync() => TryCreateNetworkAsync();

    /// <summary>Creates a network and reports whether the dialog can close.</summary>
    public async Task<bool> TryCreateNetworkAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(NetworkName)) { StatusText = LocalizedText.Get("docker.network.required"); return false; }
        var name = NetworkName.Trim();
        return await RunOperationAsync(
            () => client.CreateNetworkAsync(new DockerNetworkCreateRequest(name, SelectedNetworkDriver)),
            result => result.Success ? LocalizedText.Format("docker.network.created", name) : LocalizedText.Format("docker.network.create_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => NetworkName = string.Empty,
            operationName: LocalizedText.Format("docker.operation.create_network", name));
    }
    [RelayCommand(CanExecute = nameof(CanDeleteNetwork))] private async Task DeleteNetworkAsync()
    {
        var network = SelectedNetwork; if (network is null) return;
        await RunOperationAsync(
            () => client.DeleteNetworkAsync(network.Id, true),
            result => result.Success ? LocalizedText.Format("docker.network.deleted", network.Name) : LocalizedText.Format("docker.network.delete_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.delete_network", network.Name));
    }
    private bool CanDeleteNetwork => SelectedNetwork is not null && ConfirmNetworkDeletion && !IsLoading;
    partial void OnSelectedNetworkChanged(DockerNetworkDto? value) => DeleteNetworkCommand.NotifyCanExecuteChanged();
    partial void OnConfirmNetworkDeletionChanged(bool value) => DeleteNetworkCommand.NotifyCanExecuteChanged();

    [RelayCommand] private Task CreateVolumeAsync() => TryCreateVolumeAsync();

    /// <summary>Creates a volume and reports whether the dialog can close.</summary>
    public async Task<bool> TryCreateVolumeAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(VolumeName)) { StatusText = LocalizedText.Get("docker.volume.required"); return false; }
        var name = VolumeName.Trim();
        return await RunOperationAsync(
            () => client.CreateVolumeAsync(new DockerVolumeCreateRequest(name, SelectedVolumeDriver)),
            result => result.Success ? LocalizedText.Format("docker.volume.created", name) : LocalizedText.Format("docker.volume.create_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => VolumeName = string.Empty,
            operationName: LocalizedText.Format("docker.operation.create_volume", name));
    }
    [RelayCommand(CanExecute = nameof(CanDeleteVolume))] private async Task DeleteVolumeAsync()
    {
        var volume = SelectedVolume; if (volume is null) return;
        await RunOperationAsync(
            () => client.DeleteVolumeAsync(volume.Name, true),
            result => result.Success ? LocalizedText.Format("docker.volume.deleted", volume.Name) : LocalizedText.Format("docker.volume.delete_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.delete_volume", volume.Name));
    }
    private bool CanDeleteVolume => SelectedVolume is not null && ConfirmVolumeDeletion && !IsLoading;
    partial void OnSelectedVolumeChanged(DockerVolumeDto? value) => DeleteVolumeCommand.NotifyCanExecuteChanged();
    partial void OnConfirmVolumeDeletionChanged(bool value) => DeleteVolumeCommand.NotifyCanExecuteChanged();

    private async Task<bool> RunOperationAsync(Func<Task<DockerOperationResult>> operation, Func<DockerOperationResult, string> status, Action? onSuccess = null, string? operationName = null)
    {
        if (!await EnsureDockerAvailableAsync()) return false;
        var succeeded = false;
        IsLoading = true;
        BeginOperation(operationName);
        try
        {
            var result = await operation();
            succeeded = result.Success;
            StatusText = status(result);
            AppendOperationLog(result.LogLines);
            CompleteOperation(StatusText);
            if (result.Success) onSuccess?.Invoke();
            else await ShowUnavailableForProblemAsync(result.ProblemCode);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("docker.status.failed", exception.Message);
            AppendOperationLog([exception.Message]);
            CompleteOperation(StatusText);
            await ShowUnavailableForExceptionAsync();
        }
        finally { IsOperationRunning = false; IsLoading = false; }
        await RefreshAsync();
        return succeeded;
    }
    private async Task<bool> RunOperationAsync(Func<Task<DockerStackOperationResult>> operation, Func<DockerStackOperationResult, string> status, string? operationName = null)
    {
        if (!await EnsureDockerAvailableAsync()) return false;
        var succeeded = false;
        IsLoading = true;
        BeginOperation(operationName);
        try
        {
            var result = await operation(); succeeded = result.Success; StatusText = status(result);
            AppendOperationLog(result.Messages);
            CompleteOperation(StatusText);
            if (!result.Success) await ShowUnavailableForProblemAsync(result.ProblemCode);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("docker.status.failed", exception.Message);
            AppendOperationLog([exception.Message]);
            CompleteOperation(StatusText);
            await ShowUnavailableForExceptionAsync();
        }
        finally { IsOperationRunning = false; IsLoading = false; }
        await RefreshAsync();
        return succeeded;
    }
    private async Task RunReadAsync(Func<Task> operation)
    {
        if (!await EnsureDockerAvailableAsync()) return;
        IsLoading = true;
        BeginOperation(LocalizedText.Get("docker.operation.reading"));
        try { await operation(); CompleteOperation(StatusText); }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("docker.status.failed", exception.Message);
            AppendOperationLog([exception.Message]);
            CompleteOperation(StatusText);
            await ShowUnavailableForExceptionAsync();
        }
        finally { IsOperationRunning = false; IsLoading = false; }
    }
    private async Task<bool> EnsureDockerAvailableAsync()
    {
        if (IsDockerAvailable) return true;
        StatusText = LocalizedText.Get("docker.status.unavailable_operation");
        await ShowDockerUnavailableDialogAsync();
        return false;
    }
    private async Task ShowUnavailableForProblemAsync(string problemCode)
    {
        if (problemCode is not ("docker.unavailable" or "docker.not_installed" or "docker.api_incompatible")) return;
        IsDockerAvailable = false;
        IsDockerInstallRequired = IsInstallRequired(false, problemCode);
        StatusText = LocalizedText.Format("docker.status.unavailable", problemCode);
        await ShowDockerUnavailableDialogAsync();
    }
    private async Task ShowUnavailableForExceptionAsync()
    {
        try
        {
            var status = await client.GetStatusAsync();
            if (status.IsAvailable) return;
            IsDockerAvailable = false;
            IsDockerInstallRequired = IsInstallRequired(false, status.ProblemCode);
            StatusText = LocalizedText.Format("docker.status.unavailable", status.ProblemCode);
        }
        catch
        {
            IsDockerAvailable = false;
            IsDockerInstallRequired = false;
            StatusText = LocalizedText.Get("docker.status.unavailable_operation");
        }
        await ShowDockerUnavailableDialogAsync();
    }
    private async Task ShowDockerUnavailableDialogAsync()
    {
        if (_isUnavailableDialogShowing || ShowDockerUnavailableAsync is null) return;
        _isUnavailableDialogShowing = true;
        try { await ShowDockerUnavailableAsync(); }
        finally { _isUnavailableDialogShowing = false; }
    }
    private static bool IsInstallRequired(bool isAvailable, string? problemCode) =>
        !isAvailable && string.Equals(problemCode, "docker.not_installed", StringComparison.OrdinalIgnoreCase);
    private static string OperationText(string operation) => operation switch
    {
        "validate" => LocalizedText.Get("docker.stack.validate"),
        "deploy" => LocalizedText.Get("docker.stack.deploy"),
        "logs" => LocalizedText.Get("docker.container.logs"),
        "stats" => LocalizedText.Get("docker.container.stats"),
        _ => LocalizedText.Get($"docker.action.{operation}"),
    };
    private void BeginOperation(string? operationName)
    {
        OperationTitle = string.IsNullOrWhiteSpace(operationName) ? LocalizedText.Get("docker.operation.running") : operationName;
        OperationLog = LocalizedText.Format("docker.operation.started", OperationTitle);
        IsOperationRunning = true;
        OnPropertyChanged(nameof(HasOperationActivity));
        StatusText = LocalizedText.Format("docker.operation.running", OperationTitle);
    }
    private void AppendOperationLog(IEnumerable<string>? lines)
    {
        if (lines is null) return;
        var values = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (values.Length == 0) return;
        OperationLog = string.Join(Environment.NewLine, new[] { OperationLog }.Concat(values));
    }
    private void CompleteOperation(string outcome) =>
        OperationLog = string.Join(Environment.NewLine, new[] { OperationLog, LocalizedText.Format("docker.operation.finished", outcome) });
    private static string ProblemText(string problemCode) => problemCode switch
    {
        "docker.operation_timeout" => LocalizedText.Get("docker.problem.timeout"),
        "docker.operation_failed" => LocalizedText.Get("docker.problem.failed"),
        _ => problemCode
    };
    private static IReadOnlyList<string> Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
