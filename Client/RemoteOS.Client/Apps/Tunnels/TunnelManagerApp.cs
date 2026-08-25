using Client.Apps.Tunnels.Views;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Tunnels;

/// <summary>Single-window FRP administration UI. It manages control-plane state only, never tunnel traffic.</summary>
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
        var vm = new TunnelManagerViewModel(client, canManage); var window = context.ShowWindow(LocalizedText.Get("tunnels.title"), new TunnelManagerView { DataContext = vm }, new Rect(90, 65, 1040, 680), Manifest.IconGlyph);
        EventHandler<RemoteOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, item) => { if (!ReferenceEquals(item, window)) return; context.WindowManager.WindowClosed -= closed; vm.Dispose(); };
        context.WindowManager.WindowClosed += closed; _ = vm.StartAsync();
    }
}
