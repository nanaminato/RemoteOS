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
    public ObservableCollection<string> AvailableNetworks { get; } = ["bridge"];

    // Docker's built-in drivers that can create a user-defined network. Host and none are
    // built-in special networks, rather than choices for `docker network create`.
    public IReadOnlyList<string> NetworkDrivers { get; } = ["bridge", "ipvlan", "macvlan", "overlay"];
    public IReadOnlyList<string> VolumeDrivers { get; } = ["local"];
    public IReadOnlyList<string> RestartPolicies { get; } = ["no", "always", "unless-stopped", "on-failure"];

    [ObservableProperty] private string _statusText = LocalizedText.Get("docker.status.loading");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDockerAvailable;
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

    public int RunningContainerCount => Containers.Count(container => container.State.Equals("running", StringComparison.OrdinalIgnoreCase));

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
            await Task.WhenAll(statusTask, containersTask, imagesTask, networksTask, volumesTask);
            var status = await statusTask;
            IsDockerAvailable = status.IsAvailable;
            EngineVersion = status.ServerVersion ?? "—";
            EnginePlatform = string.Join(" / ", new[] { status.OperatingSystem, status.Architecture }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(EnginePlatform)) EnginePlatform = "—";
            StatusText = status.IsAvailable
                ? LocalizedText.Format("docker.status.available", status.ServerVersion ?? "", status.OperatingSystem ?? "")
                : LocalizedText.Format("docker.status.unavailable", status.ProblemCode);
            Replace(Containers, await containersTask); Replace(Images, await imagesTask);
            var networks = await networksTask;
            Replace(Networks, networks); Replace(Volumes, await volumesTask);
            Replace(AvailableNetworks, networks.Select(network => network.Name).Prepend("bridge").Distinct(StringComparer.Ordinal));
            if (!AvailableNetworks.Contains(ContainerNetwork, StringComparer.Ordinal)) ContainerNetwork = "bridge";
            OnPropertyChanged(nameof(RunningContainerCount));
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.status.failed", exception.Message); }
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
            StatusText = logs is null ? LocalizedText.Format("docker.action.failed", "logs", "docker.not_found") : LocalizedText.Format("docker.action.succeeded", "logs", container.Names);
        });
    }
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private async Task LoadContainerStatsAsync()
    {
        var container = SelectedContainer; if (container is null) return;
        await RunReadAsync(async () =>
        {
            var stats = await client.GetContainerStatsAsync(container.Id);
            ContainerStats = stats is null ? string.Empty : $"CPU {stats.CpuPercent}  •  Memory {stats.MemoryUsage}  •  Network {stats.NetworkIo}  •  Block I/O {stats.BlockIo}";
            StatusText = stats is null ? LocalizedText.Format("docker.action.failed", "stats", "docker.not_found") : LocalizedText.Format("docker.action.succeeded", "stats", container.Names);
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
            result => result.Success ? LocalizedText.Format("docker.action.succeeded", action, container.Names) : LocalizedText.Format("docker.action.failed", action, result.ProblemCode));
    }

    [RelayCommand] private Task ValidateStackAsync() => ApplyStackAsync("validate");
    [RelayCommand] private Task DeployStackAsync() => ApplyStackAsync("deploy");
    [RelayCommand] private Task DownStackAsync() => ApplyStackAsync("down");
    private async Task ApplyStackAsync(string operation)
    {
        if (string.IsNullOrWhiteSpace(StackName) || string.IsNullOrWhiteSpace(ComposeYaml)) { StatusText = LocalizedText.Get("docker.stack.required"); return; }
        await RunStackOperationAsync(operation);
    }
    private async Task RunStackOperationAsync(string operation)
    {
        await RunOperationAsync(
            () => client.ApplyStackOperationAsync(operation, new DockerStackDefinitionDto(StackName.Trim(), ComposeYaml)),
            result =>
            {
                var detail = result.Messages.FirstOrDefault() ?? result.ProblemCode;
                return result.Success ? LocalizedText.Format("docker.stack.succeeded", operation, StackName) : LocalizedText.Format("docker.stack.failed", operation, detail);
            });
    }

    [RelayCommand] private async Task PullImageAsync()
    {
        if (string.IsNullOrWhiteSpace(ImageReference)) { StatusText = LocalizedText.Get("docker.image.required"); return; }
        var imageReference = ImageReference.Trim();
        await RunOperationAsync(
            () => client.PullImageAsync(new DockerImageOperationRequest(imageReference)),
            result => result.Success ? LocalizedText.Format("docker.image.pull_succeeded", imageReference) : LocalizedText.Format("docker.image.pull_failed", result.ProblemCode));
    }

    [RelayCommand] private async Task CreateContainerAsync()
    {
        if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ContainerImage)) { StatusText = LocalizedText.Get("docker.container.required"); return; }
        var name = ContainerName.Trim();
        await RunOperationAsync(
            () => client.CreateContainerAsync(new DockerContainerCreateRequest(
                name, ContainerImage.Trim(), Lines(ContainerArguments), Lines(ContainerPorts), Lines(ContainerEnvironment), Lines(ContainerMounts), ContainerNetwork, ContainerRestartPolicy)),
            result => result.Success ? LocalizedText.Format("docker.container.created", name) : LocalizedText.Format("docker.container.create_failed", result.ProblemCode),
            onSuccess: () =>
            {
                ContainerName = ContainerImage = ContainerArguments = ContainerPorts = ContainerEnvironment = ContainerMounts = string.Empty;
                ContainerNetwork = "bridge"; ContainerRestartPolicy = "unless-stopped";
            });
    }

    [RelayCommand(CanExecute = nameof(CanDeleteImage))] private async Task DeleteImageAsync()
    {
        var image = SelectedImage; if (image is null) return;
        await RunOperationAsync(
            () => client.DeleteImageAsync(image.Id, new DockerImageOperationRequest(image.Id, true)),
            result => result.Success ? LocalizedText.Format("docker.image.deleted", image.Repository) : LocalizedText.Format("docker.image.delete_failed", result.ProblemCode));
    }
    private bool CanDeleteImage => SelectedImage is not null && ConfirmImageDeletion && !IsLoading;
    partial void OnSelectedImageChanged(DockerImageDto? value) => DeleteImageCommand.NotifyCanExecuteChanged();
    partial void OnConfirmImageDeletionChanged(bool value) => DeleteImageCommand.NotifyCanExecuteChanged();

    [RelayCommand] private async Task CreateNetworkAsync()
    {
        if (string.IsNullOrWhiteSpace(NetworkName)) { StatusText = LocalizedText.Get("docker.network.required"); return; }
        var name = NetworkName.Trim();
        await RunOperationAsync(
            () => client.CreateNetworkAsync(new DockerNetworkCreateRequest(name, SelectedNetworkDriver)),
            result => result.Success ? LocalizedText.Format("docker.network.created", name) : LocalizedText.Format("docker.network.create_failed", result.ProblemCode),
            onSuccess: () => NetworkName = string.Empty);
    }
    [RelayCommand(CanExecute = nameof(CanDeleteNetwork))] private async Task DeleteNetworkAsync()
    {
        var network = SelectedNetwork; if (network is null) return;
        await RunOperationAsync(
            () => client.DeleteNetworkAsync(network.Id, true),
            result => result.Success ? LocalizedText.Format("docker.network.deleted", network.Name) : LocalizedText.Format("docker.network.delete_failed", result.ProblemCode));
    }
    private bool CanDeleteNetwork => SelectedNetwork is not null && ConfirmNetworkDeletion && !IsLoading;
    partial void OnSelectedNetworkChanged(DockerNetworkDto? value) => DeleteNetworkCommand.NotifyCanExecuteChanged();
    partial void OnConfirmNetworkDeletionChanged(bool value) => DeleteNetworkCommand.NotifyCanExecuteChanged();

    [RelayCommand] private async Task CreateVolumeAsync()
    {
        if (string.IsNullOrWhiteSpace(VolumeName)) { StatusText = LocalizedText.Get("docker.volume.required"); return; }
        var name = VolumeName.Trim();
        await RunOperationAsync(
            () => client.CreateVolumeAsync(new DockerVolumeCreateRequest(name, SelectedVolumeDriver)),
            result => result.Success ? LocalizedText.Format("docker.volume.created", name) : LocalizedText.Format("docker.volume.create_failed", result.ProblemCode),
            onSuccess: () => VolumeName = string.Empty);
    }
    [RelayCommand(CanExecute = nameof(CanDeleteVolume))] private async Task DeleteVolumeAsync()
    {
        var volume = SelectedVolume; if (volume is null) return;
        await RunOperationAsync(
            () => client.DeleteVolumeAsync(volume.Name, true),
            result => result.Success ? LocalizedText.Format("docker.volume.deleted", volume.Name) : LocalizedText.Format("docker.volume.delete_failed", result.ProblemCode));
    }
    private bool CanDeleteVolume => SelectedVolume is not null && ConfirmVolumeDeletion && !IsLoading;
    partial void OnSelectedVolumeChanged(DockerVolumeDto? value) => DeleteVolumeCommand.NotifyCanExecuteChanged();
    partial void OnConfirmVolumeDeletionChanged(bool value) => DeleteVolumeCommand.NotifyCanExecuteChanged();

    private async Task RunOperationAsync(Func<Task<DockerOperationResult>> operation, Func<DockerOperationResult, string> status, Action? onSuccess = null)
    {
        IsLoading = true;
        try
        {
            var result = await operation();
            StatusText = status(result);
            if (result.Success) onSuccess?.Invoke();
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.status.failed", exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }
    private async Task RunOperationAsync(Func<Task<DockerStackOperationResult>> operation, Func<DockerStackOperationResult, string> status)
    {
        IsLoading = true;
        try { var result = await operation(); StatusText = status(result); }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.status.failed", exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }
    private async Task RunReadAsync(Func<Task> operation)
    {
        IsLoading = true;
        try { await operation(); }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.status.failed", exception.Message); }
        finally { IsLoading = false; }
    }
    private static IReadOnlyList<string> Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
