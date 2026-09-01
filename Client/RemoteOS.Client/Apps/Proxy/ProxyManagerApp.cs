using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Client.Apps.Explorer;
using Client.Apps.Explorer.ViewModels;
using Client.Apps.Explorer.Views;
using Client.Apps.Proxy.Views;
using Client.Localization;
using Client.Services.Auth;
using Client.Views;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;
using Rect = RemoteOS.Core.Primitives.Rect;

namespace Client.Apps.Proxy;

/// <summary>Built-in host-global proxy workspace. All actions traverse the RemoteOS API.</summary>
public sealed class ProxyManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.proxy"), "Proxy Manager", "1.0.0", "⇄", "Manage the host Proxy runtime and recovery state",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage, AppPermissions.ServerProxyTunManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var repository = context.Services.GetService(typeof(IProxyRepository)) as IProxyRepository;
        if (session is null || repository is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.proxy.display_name"), new TextBlock { Text = LocalizedText.Get("proxy.login_required"), Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false); return;
        }
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        // Do not start a proxy operation with the initial Undecided permission snapshot.
        // ApplicationManager presents prompts after Activate returns, so this workspace owns
        // the first request and waits for its decision before enabling server actions.
        var vm = new ProxyManagerViewModel(repository, canManage: false, canManageTun: false);
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.proxy.display_name"), new ProxyManagerWorkspace(vm), new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        vm.SetServerRuntimePackageRequest(async () =>
        {
            if (files is null) return null;
            return await context.ShowDialogAsync<string?>(window, LocalizedText.Get("proxy.runtime.select_server_package"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [new ExplorerFileFilter(LocalizedText.Get("proxy.runtime.package_filter"), ["*.zip", "*.gz"])]),
                    paths => dialog.Close(paths.FirstOrDefault()))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, new RemoteOS.Core.Primitives.Size(720, 520));
        });
        vm.ShowRuntimeDownloadUrlAsync = url => ShowDownloadUrlAsync(LocalizedText.Get("proxy.runtime_download_title"), url);
        EventHandler<RemoteOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) => { if (ReferenceEquals(current, window)) context.WindowManager.WindowClosed -= closed; };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAfterPermissionDecisionAsync();

        async Task InitializeAfterPermissionDecisionAsync()
        {
            var read = await RequestIfUndecidedAsync(AppPermissions.ServerProxyRead);
            var manage = await RequestIfUndecidedAsync(AppPermissions.ServerProxyManage);
            var tun = await RequestIfUndecidedAsync(AppPermissions.ServerProxyTunManage);
            vm.SetPermissions(manage, tun);

            // A declined read grant still permits the workspace to render its normal API
            // authorization result, but it never enables a mutation solely because the prompt
            // happened to complete after activation.
            _ = read;
            await vm.StartAsync();
        }

        async Task<bool> RequestIfUndecidedAsync(string permission)
        {
            var decision = context.Permissions.GetStatus(permission);
            if (decision == AppPermissionStatus.Undecided)
                decision = await context.Permissions.RequestAsync(permission);
            return decision == AppPermissionStatus.Granted;
        }

        Task ShowDownloadUrlAsync(string title, string url) => context.ShowDialogAsync<bool?>(window, title, dialog => new DownloadUrlDialogView
        {
            DataContext = new DownloadUrlDialogViewModel(url, CopyToClipboardAsync, () => dialog.Close(true)),
        }, new RemoteOS.Core.Primitives.Size(660, 210));

        async Task CopyToClipboardAsync(string value)
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            if (topLevel?.Clipboard is not null) await topLevel.Clipboard.SetTextAsync(value);
        }
    }
}
