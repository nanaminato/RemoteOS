using Client.Apps.Git.Views;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.Common;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Git;

/// <summary>Built-in client for managing Git repositories on the RemoteOS Server.</summary>
public sealed class GitClientApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.git"), "Git Client", "0.1.0", "\U0001f33f",
        "Manage Git repositories on the RemoteOS Server",
        [AppPermissions.ServerGitRead, AppPermissions.ServerGitManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteGitClient)) as IRemoteGitClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.git.display_name"),
                new GitLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var vm = new GitClientViewModel(client);
        ManagedWindow? window = null;

        // Wire dialog callbacks — the VM commands call these and handle the returned request objects.
        vm.ShowCommitDialogAsync = () => GitClientDialogs.ShowCommitDialogAsync(context, window!, vm);
        vm.ShowCreateBranchDialogAsync = () => GitClientDialogs.ShowCreateBranchDialogAsync(context, window!, vm);
        vm.ShowPullDialogAsync = () => GitClientDialogs.ShowPullDialogAsync(context, window!, vm);
        vm.ShowRegisterRepositoryDialogAsync = () => GitClientDialogs.ShowRegisterRepositoryDialogAsync(context, window!, vm);
        vm.ShowConfirmAsync = message => GitClientDialogs.ShowConfirmAsync(context, window!, message);
        vm.ShowGitUnavailableAsync = async () =>
        {
            var shouldContinue = await GitClientDialogs.ShowGitUnavailableAsync(context, window!, vm);
            if (!shouldContinue)
            {
                // User cancelled / exited: close the window to leave the app.
                context.WindowManager.Close(window!);
            }
        };

        var view = GitClientWorkspace.Create(vm);
        window = context.ShowWindow(LocalizedText.Get("application.remoteos.git.display_name"),
            view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        _ = vm.StartAsync();
    }
}
