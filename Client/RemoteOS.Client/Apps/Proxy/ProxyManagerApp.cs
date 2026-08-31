using Avalonia;
using Avalonia.Controls;
using Client.Apps.Explorer;
using Client.Apps.Explorer.ViewModels;
using Client.Apps.Explorer.Views;
using Client.Apps.Proxy.Views;
using Client.Localization;
using Client.Services.Auth;
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
            context.ShowWindow(LocalizedText.Get("proxy.title"), new TextBlock { Text = LocalizedText.Get("proxy.login_required"), Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false); return;
        }
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var vm = new ProxyManagerViewModel(repository, context.Permissions.IsGranted(AppPermissions.ServerProxyManage), context.Permissions.IsGranted(AppPermissions.ServerProxyTunManage));
        var window = context.ShowWindow(LocalizedText.Get("proxy.title"), new ProxyManagerWorkspace(vm), new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
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
        EventHandler<RemoteOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) => { if (ReferenceEquals(current, window)) context.WindowManager.WindowClosed -= closed; };
        context.WindowManager.WindowClosed += closed; _ = vm.StartAsync();
    }
}
