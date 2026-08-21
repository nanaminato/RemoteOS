using Client.Apps.Explorer;
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

/// <summary>Built-in client for managing Git repositories on the RemoteOS Server.
/// 每个 Git Client 窗口都是一个独立项目实例（MultiWindow）——启动时显示项目选择器，
/// 用户从已注册项目打开，或新打开一个远程文件夹作为新项目；多个项目可同时在不同窗口运行。</summary>
public sealed class GitClientApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.git"), "Git Client", "0.2.0", "\U0001f33f",
        "Manage Git repositories on the RemoteOS Server",
        [AppPermissions.ServerGitRead, AppPermissions.ServerGitManage],
        InstancePolicy: ApplicationInstancePolicy.MultiWindow);

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

        // 复用 Explorer 的 IExplorerClient，用于远程文件夹选择对话框
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
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

        // 新增：远程路径选择器、初始化确认、远程编辑对话框
        vm.ShowRemotePathPickerAsync = () =>
            files is null
                ? Task.FromResult<string?>(null)
                : GitClientDialogs.ShowRemotePathPickerAsync(context, window!, files);
        vm.ShowInitConfirmAsync = path => GitClientDialogs.ShowInitConfirmAsync(context, window!, path);
        vm.ShowRemoteDialogAsync = existing => GitClientDialogs.ShowRemoteDialogAsync(context, window!, existing);

        var view = GitClientWorkspace.Create(vm);
        window = context.ShowWindow(LocalizedText.Get("application.remoteos.git.display_name"),
            view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        _ = vm.StartAsync();
    }
}
