using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Welcome;

/// <summary>Built-in "Welcome" application — a first-run intro window.</summary>
public sealed class WelcomeApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.welcome"),
        DisplayName: "Welcome",
        Version: "1.0.0",
        IconGlyph: "🏠",
        Description: "Get started with RemoteOS");

    public override void Activate(AppContext context)
    {
        var view = new WelcomeView { DataContext = new WelcomeViewModel() };
        context.ShowWindow("Welcome to RemoteOS", view,
            bounds: new Rect(120, 80, 720, 480),
            iconGlyph: Manifest.IconGlyph);
    }
}
