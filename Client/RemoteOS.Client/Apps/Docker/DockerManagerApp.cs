using Client.Apps.Docker.Views;
using Client.Localization;
using Client.Services;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Docker;

/// <summary>Built-in client for the server-local Docker Engine.</summary>
public sealed class DockerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.docker"), "Docker Manager", "0.2.0", "🐳", "Manage the local Docker Engine on the RemoteOS Server", [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteDockerClient)) as IRemoteDockerClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.docker.display_name"),
                new DockerLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var vm = new DockerManagerViewModel(client);
        ManagedWindow? window = null;
        var view = DockerManagerWorkspace.Create(vm,
            () => DockerManagerDialogs.ShowCreateContainerAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowDeployStackAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowPullImageAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowCreateNetworkAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowCreateVolumeAsync(context, window!, vm));
        window = context.ShowWindow(LocalizedText.Get("application.remoteos.docker.display_name"), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        vm.ShowDockerUnavailableAsync = () => DockerManagerDialogs.ShowDockerUnavailableAsync(context, window, vm);
        vm.OpenDockerInstallGuideAsync = () =>
        {
            var language = (context.Services.GetService(typeof(ISystemLanguage)) as ISystemLanguage)?.CurrentLanguage ?? "en-US";
            var uri = new Uri($"help://guide/docker/install?lang={Uri.EscapeDataString(language)}");
            (context.Services.GetService(typeof(IAppActivationDiagnostics)) as IAppActivationDiagnostics)
                ?.Record($"Docker Manager requested installation guide: uri={uri.Scheme}://{uri.Host}{uri.AbsolutePath}, language={language}.");
            var activation = context.Activations.Activate(uri);
            (context.Services.GetService(typeof(IAppActivationDiagnostics)) as IAppActivationDiagnostics)
                ?.Record($"Docker Manager installation guide activation result: status={activation.Status}, target={activation.TargetAppId?.Value ?? "<none>"}.");
            if (!activation.Succeeded && !activation.IsPendingUserChoice)
                vm.StatusText = LocalizedText.Get("docker.status.install_guide_unavailable");
            return Task.CompletedTask;
        };
        _ = vm.StartAsync();
    }
}
