using Client.Services.DesktopRestore;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Terminal;

/// <summary>Restores already-running server PTYs when an authenticated desktop becomes ready.</summary>
public sealed class TerminalDesktopRestoreParticipant : IDesktopRestoreParticipant
{
    private readonly TerminalApp _terminal;
    private readonly IWindowManager _windowManager;
    private readonly IServiceProvider _services;

    public TerminalDesktopRestoreParticipant(
        TerminalApp terminal,
        IWindowManager windowManager,
        IServiceProvider services)
    {
        _terminal = terminal;
        _windowManager = windowManager;
        _services = services;
    }

    public int Order => 100;

    public Task RestoreAsync(DesktopRestoreContext context, CancellationToken cancellationToken)
    {
        if (context.Session.State != AuthSessionState.Authenticated)
            return Task.CompletedTask;

        var appContext = new AppContext(_terminal.Manifest.Id, _windowManager, _services);
        return _terminal.RestoreExistingSessionsAsync(appContext, cancellationToken);
    }
}
