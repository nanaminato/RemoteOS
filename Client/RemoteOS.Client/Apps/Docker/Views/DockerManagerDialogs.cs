using RemoteOS.AppSDK;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Docker.Views;

/// <summary>Opens the Docker AXAML dialog views at their intended sizes.</summary>
internal static class DockerManagerDialogs
{
    public static Task ShowCreateContainerAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, Client.Localization.LocalizedText.Get("docker.container.create"), dialog => new DockerContainerDialogView(vm, dialog), new RemoteOS.Core.Primitives.Size(720, 760));

    public static Task ShowDeployStackAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, Client.Localization.LocalizedText.Get("docker.stack.deploy"), dialog => new DockerStackDialogView(vm, dialog), new RemoteOS.Core.Primitives.Size(760, 670));

    public static Task ShowPullImageAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, Client.Localization.LocalizedText.Get("docker.image.pull"), dialog => new DockerPullImageDialogView(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 390));

    public static Task ShowCreateNetworkAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, Client.Localization.LocalizedText.Get("common.create"), dialog => new DockerNetworkDialogView(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 420));

    public static Task ShowCreateVolumeAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, Client.Localization.LocalizedText.Get("common.create"), dialog => new DockerVolumeDialogView(vm, dialog), new RemoteOS.Core.Primitives.Size(470, 420));

    public static Task ShowDockerUnavailableAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, Client.Localization.LocalizedText.Get("docker.unavailable_dialog.title"), dialog => new DockerUnavailableDialogView(vm, dialog), new RemoteOS.Core.Primitives.Size(460, 220));
}
