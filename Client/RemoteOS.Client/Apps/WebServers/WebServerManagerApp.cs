using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
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
            return await ConfirmAsync("webservers.integration.confirmation.title", "webservers.integration.confirmation.message", "webservers.integration.confirmation.confirm");
        };
        viewModel.RequestManagedInstallConfirmationAsync = () => ConfirmAsync("webservers.managed.install.title", "webservers.managed.install.message", "webservers.managed.install.confirm");
        viewModel.RequestManagedUninstallConfirmationAsync = () => ConfirmAsync("webservers.managed.uninstall.title", "webservers.managed.uninstall.message", "webservers.managed.uninstall.confirm");
        viewModel.RequestLocalNginxPackageAsync = async () =>
        {
            var topLevel = GetTopLevel();
            if (topLevel is null) return null;
            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("webservers.managed.select_package"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(LocalizedText.Get("webservers.managed.package_file_type")) { Patterns = ["*.zip"] }],
            });
            return selected.FirstOrDefault()?.TryGetLocalPath();
        };

        async Task<bool> ConfirmAsync(string titleKey, string messageKey, string confirmKey)
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool?>(window, LocalizedText.Get(titleKey), dialog =>
            {
                var dialogViewModel = new NginxIntegrationConfirmationDialogViewModel(result =>
                {
                    confirmed = result;
                    dialog.Close(result);
                }, messageKey, confirmKey);
                return new NginxIntegrationConfirmationDialogView { DataContext = dialogViewModel };
            }, new Size(500, 220));
            return confirmed;
        }
        _ = viewModel.StartAsync();
    }

    private static TopLevel? GetTopLevel() => Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        ? desktop.MainWindow
        : null;
}
