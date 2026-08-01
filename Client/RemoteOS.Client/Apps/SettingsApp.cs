using Client.Services;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps;

/// <summary>Built-in Settings application — personalizes the shell appearance.</summary>
public sealed class SettingsApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.settings"),
        DisplayName: "Settings",
        Version: "1.0.0",
        IconGlyph: "⚙️",
        Description: "Personalize RemoteOS");

    public override void Activate(AppContext context)
    {
        var settings = context.Services.GetRequiredService<ShellSettings>();
        var view = new SettingsView { DataContext = new SettingsViewModel(settings) };
        context.ShowWindow("Settings", view,
            bounds: new Rect(200, 120, 640, 480),
            iconGlyph: Manifest.IconGlyph);
    }
}
