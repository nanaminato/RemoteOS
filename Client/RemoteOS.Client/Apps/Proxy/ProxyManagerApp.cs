using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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
        var vm = new ProxyManagerViewModel(repository, context.Permissions.IsGranted(AppPermissions.ServerProxyManage), context.Permissions.IsGranted(AppPermissions.ServerProxyTunManage));
        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true, DataContext = vm };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        controls.Children.Add(Button("common.refresh", vm.RefreshCommand)); controls.Children.Add(Button("proxy.start", vm.StartProxyCommand)); controls.Children.Add(Button("proxy.stop", vm.StopProxyCommand)); controls.Children.Add(Button("proxy.restart", vm.RestartProxyCommand)); controls.Children.Add(Button("proxy.emergency_disable", vm.EmergencyDisableCommand));
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText))); DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        root.Children.Add(new DataGrid { ItemsSource = vm.Profiles, AutoGenerateColumns = true, IsReadOnly = true });
        var window = context.ShowWindow(LocalizedText.Get("proxy.title"), root, new Rect(100, 80, 920, 620), Manifest.IconGlyph);
        EventHandler<RemoteOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) => { if (ReferenceEquals(current, window)) context.WindowManager.WindowClosed -= closed; };
        context.WindowManager.WindowClosed += closed; _ = vm.StartAsync();
    }
    private static Button Button(string key, System.Windows.Input.ICommand command) => new() { Content = LocalizedText.Get(key), Command = command };
}
