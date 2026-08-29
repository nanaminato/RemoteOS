using Avalonia;
using Avalonia.Controls;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;
using Rect = RemoteOS.Core.Primitives.Rect;

namespace Client.Apps.Registry;

/// <summary>Built-in registry editor for the current user's RemoteOS configuration hive.</summary>
public sealed class RegistryApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.registry"), "Registry", "1.0.0", "⚙", "Browse supported RemoteOS configuration values", [AppPermissions.ServerRegistryRead, AppPermissions.ServerRegistryWrite], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRegistryClient)) as IRegistryClient;
        if (session?.State != AuthSessionState.Authenticated || client is null)
        {
            context.ShowWindow("Registry", new TextBlock { Text = "Sign in to browse the configuration registry.", Margin = new Thickness(24) }, new Rect(200, 160, 460, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new RegistryViewModel(client);
        var window = context.ShowWindow("Registry", new RegistryView { DataContext = viewModel }, new Rect(80, 60, 1120, 700), Manifest.IconGlyph);
        viewModel.ShowEditDialogAsync = async row =>
        {
            var saved = await context.ShowDialogAsync<bool>(window, "Edit Registry Value", dialog => new RegistryValueDialogView
            {
                DataContext = new RegistryValueDialogViewModel(row, client, dialog.Close),
            });
            if (saved) await viewModel.RefreshAsync();
        };
        _ = viewModel.RefreshAsync();
    }
}
