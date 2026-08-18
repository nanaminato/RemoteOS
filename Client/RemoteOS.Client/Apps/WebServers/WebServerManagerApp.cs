using Client.Apps.WebServers.Views;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.WebServers;

/// <summary>Built-in web server manager. Host-global Nginx discovery, config test, integrate, reload.</summary>
public sealed class WebServerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.webservers"), "Web Server Manager", "0.1.0", "🌐", "Manage web servers on the RemoteOS Server",
        [AppPermissions.ServerWebServersRead, AppPermissions.ServerWebServersManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteWebServerClient)) as IRemoteWebServerClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.webservers.display_name"),
                new WebServerLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new WebServerManagerViewModel(client, session, context.Permissions);
        var view = WebServerManagerWorkspace.Create(viewModel);
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.webservers.display_name"),
            view, new Rect(70, 55, 1080, 680), Manifest.IconGlyph);
        viewModel.RequestIntegrationConfirmationAsync = async () =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool?>(window, LocalizedText.Get("webservers.integration.confirmation.title"), dialog =>
            {
                var dialogViewModel = new NginxIntegrationConfirmationDialogViewModel(result =>
                {
                    confirmed = result;
                    dialog.Close(result);
                });
                return new NginxIntegrationConfirmationDialogView { DataContext = dialogViewModel };
            }, new Size(500, 190));
            return confirmed;
        };
        _ = viewModel.StartAsync();
    }
}
