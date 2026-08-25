using Client.Apps.Tunnels.Views;
using Client.Apps.Explorer;
using Client.Apps.Explorer.ViewModels;
using Client.Apps.Explorer.Views;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Tunnels;

/// <summary>FRP administration workspace with separate editors and independently refreshable log windows.</summary>
public sealed class TunnelManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.tunnels"), "Tunnel Manager", "1.1.0", "↔", "Manage FRP tunnel desired state and runtime status", [AppPermissions.ServerTunnelsRead, AppPermissions.ServerTunnelsManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteTunnelClient)) as IRemoteTunnelClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("tunnels.title"), new TunnelLoginRequiredView(), new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false); return;
        }
        var canManage = context.Permissions.IsGranted(AppPermissions.ServerTunnelsManage);
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var vm = new TunnelManagerViewModel(client, canManage); var window = context.ShowWindow(LocalizedText.Get("tunnels.title"), new TunnelManagerView { DataContext = vm }, new Rect(90, 65, 1040, 680), Manifest.IconGlyph);
        vm.RequestServerRuntimePackageAsync = async () =>
        {
            if (files is null) return null;
            return await context.ShowDialogAsync<string?>(window, LocalizedText.Get("tunnels.runtime.select_server_package"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [new ExplorerFileFilter(LocalizedText.Get("tunnels.runtime.package_filter"), ["*.zip", "*.tar.gz", "*.tgz"])]),
                    paths => dialog.Close(paths.FirstOrDefault()))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, new Size(720, 520));
        };
        vm.ShowOfficialRuntimeDownloadPageAsync = () =>
        {
            context.Activations.Activate(new Uri("https://github.com/fatedier/frp/releases"));
            return Task.CompletedTask;
        };
        vm.OpenProfileEditorAsync = profile =>
        {
            var editor = new TunnelProfileEditorViewModel(client, profile) { SavedAsync = vm.RefreshAfterChildAsync };
            var editorWindow = context.ShowWindow(LocalizedText.Get(profile is null ? "tunnels.server.new_title" : "tunnels.server.edit_title"),
                new TunnelProfileEditorView { DataContext = editor }, new Rect(200, 150, 620, 540), Manifest.IconGlyph);
            editor.CloseAsync = () => { context.WindowManager.Close(editorWindow); return Task.CompletedTask; };
            return Task.CompletedTask;
        };
        vm.OpenTunnelEditorAsync = tunnel =>
        {
            var editor = new TunnelDefinitionEditorViewModel(client, vm.Profiles, tunnel) { SavedAsync = vm.RefreshAfterChildAsync };
            var editorWindow = context.ShowWindow(LocalizedText.Get(tunnel is null ? "tunnels.tunnel.new_title" : "tunnels.tunnel.edit_title"),
                new TunnelDefinitionEditorView { DataContext = editor }, new Rect(240, 145, 650, 520), Manifest.IconGlyph);
            editor.CloseAsync = () => { context.WindowManager.Close(editorWindow); return Task.CompletedTask; };
            return Task.CompletedTask;
        };
        vm.OpenLogsWindowAsync = profile =>
        {
            var logViewModel = new TunnelLogViewModel(client, profile);
            var logWindow = context.ShowWindow(LocalizedText.Format("tunnels.logs.title_format", profile.Name),
                new TunnelLogWindowView { DataContext = logViewModel }, new Rect(270, 175, 720, 510), Manifest.IconGlyph);
            EventHandler<RemoteOS.WindowManager.ManagedWindow>? logClosed = null;
            logClosed = (_, item) =>
            {
                if (!ReferenceEquals(item, logWindow)) return;
                context.WindowManager.WindowClosed -= logClosed;
                logViewModel.Dispose();
            };
            context.WindowManager.WindowClosed += logClosed;
            _ = logViewModel.StartAsync();
            return Task.CompletedTask;
        };
        EventHandler<RemoteOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, item) => { if (!ReferenceEquals(item, window)) return; context.WindowManager.WindowClosed -= closed; vm.Dispose(); };
        context.WindowManager.WindowClosed += closed; _ = vm.StartAsync();
    }
}
