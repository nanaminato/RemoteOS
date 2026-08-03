using Client.Services.Auth;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps;

/// <summary>
/// Built-in Terminal application. Hosts the RoyalTerminal <c>TerminalControl</c> inside a
/// RemoteWindow and starts a <b>remote</b> PTY session over SignalR (Server-side PTY, JWT via
/// <see cref="IAuthSession"/>); falls back to a local PTY when unauthenticated. See
/// <see cref="TerminalView"/> for lifecycle wiring and <see cref="TerminalViewModel"/> for transport.
/// </summary>
public sealed class TerminalApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.terminal"),
        DisplayName: "Terminal",
        Version: "1.0.0",
        IconGlyph: "🖥",
        Description: "远端终端");

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService<IAuthSession>();
        var viewModel = new TerminalViewModel(session);
        var view = new TerminalView { DataContext = viewModel };
        context.ShowWindow("Terminal", view,
            bounds: new Rect(120, 80, 820, 540),
            iconGlyph: Manifest.IconGlyph);
    }
}
