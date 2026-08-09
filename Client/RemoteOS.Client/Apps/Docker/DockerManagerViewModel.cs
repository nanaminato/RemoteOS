using System.Collections.ObjectModel;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Docker;

namespace Client.Apps.Docker;

public sealed partial class DockerManagerViewModel(IRemoteDockerClient client) : ObservableObject
{
    public ObservableCollection<DockerContainerDto> Containers { get; } = [];
    public ObservableCollection<DockerImageDto> Images { get; } = [];
    public ObservableCollection<DockerNetworkDto> Networks { get; } = [];
    public ObservableCollection<DockerVolumeDto> Volumes { get; } = [];

    [ObservableProperty] private string _statusText = LocalizedText.Get("docker.status.loading");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private DockerContainerDto? _selectedContainer;
    [ObservableProperty] private string _stackName = string.Empty;
    [ObservableProperty] private string _composeYaml = "services:\n  example:\n    image: hello-world";
    [ObservableProperty] private string _imageReference = string.Empty;
    [ObservableProperty] private string _containerName = string.Empty;
    [ObservableProperty] private string _containerImage = string.Empty;
    [ObservableProperty] private string _containerArguments = string.Empty;
    [ObservableProperty] private DockerImageDto? _selectedImage;
    [ObservableProperty] private bool _confirmImageDeletion;

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
            StatusText = status.IsAvailable
                ? LocalizedText.Format("docker.status.available", status.ServerVersion ?? "", status.OperatingSystem ?? "")
                : LocalizedText.Format("docker.status.unavailable", status.ProblemCode);
            Replace(Containers, await containersTask); Replace(Images, await imagesTask);
            Replace(Networks, await networksTask); Replace(Volumes, await volumesTask);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.status.failed", exception.Message); }
        finally { IsLoading = false; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear(); foreach (var value in values) target.Add(value);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedContainer))]
    private Task StartContainerAsync() => ApplyActionAsync("start");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))]
    private Task StopContainerAsync() => ApplyActionAsync("stop");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))]
    private Task RestartContainerAsync() => ApplyActionAsync("restart");

    private bool HasSelectedContainer => SelectedContainer is not null && !IsLoading;
    partial void OnSelectedContainerChanged(DockerContainerDto? value) => NotifyContainerCommands();
    partial void OnIsLoadingChanged(bool value) => NotifyContainerCommands();
    private void NotifyContainerCommands()
    {
        StartContainerCommand.NotifyCanExecuteChanged(); StopContainerCommand.NotifyCanExecuteChanged(); RestartContainerCommand.NotifyCanExecuteChanged();
    }
    private async Task ApplyActionAsync(string action)
    {
        var container = SelectedContainer; if (container is null) return;
        IsLoading = true;
        try
        {
            var result = await client.ApplyContainerActionAsync(container.Id, action, new DockerContainerActionRequest());
            StatusText = result.Success ? LocalizedText.Format("docker.action.succeeded", action, container.Names) : LocalizedText.Format("docker.action.failed", action, result.ProblemCode);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.action.failed", action, exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }

    [RelayCommand] private Task ValidateStackAsync() => ApplyStackAsync("validate");
    [RelayCommand] private Task DeployStackAsync() => ApplyStackAsync("deploy");
    [RelayCommand] private Task DownStackAsync() => ApplyStackAsync("down");
    private async Task ApplyStackAsync(string operation)
    {
        if (string.IsNullOrWhiteSpace(StackName) || string.IsNullOrWhiteSpace(ComposeYaml)) { StatusText = LocalizedText.Get("docker.stack.required"); return; }
        IsLoading = true;
        try
        {
            var result = await client.ApplyStackOperationAsync(operation, new DockerStackDefinitionDto(StackName.Trim(), ComposeYaml));
            var detail = result.Messages.FirstOrDefault() ?? result.ProblemCode;
            StatusText = result.Success ? LocalizedText.Format("docker.stack.succeeded", operation, StackName) : LocalizedText.Format("docker.stack.failed", operation, detail);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.stack.failed", operation, exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }

    [RelayCommand] private async Task PullImageAsync()
    {
        if (string.IsNullOrWhiteSpace(ImageReference)) { StatusText = LocalizedText.Get("docker.image.required"); return; }
        IsLoading = true;
        try { var result = await client.PullImageAsync(new DockerImageOperationRequest(ImageReference.Trim())); StatusText = result.Success ? LocalizedText.Format("docker.image.pull_succeeded", ImageReference) : LocalizedText.Format("docker.image.pull_failed", result.ProblemCode); }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.image.pull_failed", exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }

    [RelayCommand] private async Task CreateContainerAsync()
    {
        if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ContainerImage)) { StatusText = LocalizedText.Get("docker.container.required"); return; }
        IsLoading = true;
        try
        {
            var arguments = ContainerArguments.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await client.CreateContainerAsync(new DockerContainerCreateRequest(ContainerName.Trim(), ContainerImage.Trim(), arguments));
            StatusText = result.Success ? LocalizedText.Format("docker.container.created", ContainerName) : LocalizedText.Format("docker.container.create_failed", result.ProblemCode);
            if (result.Success) ContainerName = ContainerImage = ContainerArguments = string.Empty;
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.container.create_failed", exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteImage))] private async Task DeleteImageAsync()
    {
        var image = SelectedImage; if (image is null) return;
        IsLoading = true;
        try { var result = await client.DeleteImageAsync(image.Id, new DockerImageOperationRequest(image.Id, true)); StatusText = result.Success ? LocalizedText.Format("docker.image.deleted", image.Repository) : LocalizedText.Format("docker.image.delete_failed", result.ProblemCode); }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.image.delete_failed", exception.Message); }
        finally { IsLoading = false; }
        await RefreshAsync();
    }
    private bool CanDeleteImage => SelectedImage is not null && ConfirmImageDeletion && !IsLoading;
    partial void OnSelectedImageChanged(DockerImageDto? value) => DeleteImageCommand.NotifyCanExecuteChanged();
    partial void OnConfirmImageDeletionChanged(bool value) => DeleteImageCommand.NotifyCanExecuteChanged();
}
