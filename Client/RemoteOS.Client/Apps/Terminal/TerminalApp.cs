using Client.Services.Auth;
using Client.Services.Diagnostics;
using Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RoyalTerminal.Terminal;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Terminal;

/// <summary>
/// Built-in terminal application. A restored server session always receives its own desktop
/// window; the terminal never multiplexes multiple sessions inside a single window.
/// </summary>
public sealed class TerminalApp : RemoteApplicationBase
{
    private static int _opening;

    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.terminal"),
        DisplayName: "Terminal",
        Version: "1.0.0",
        IconGlyph: "🖥",
        Description: "远程终端");

    public override void Activate(AppContext context)
    {
        if (Interlocked.Exchange(ref _opening, 1) != 0)
            return;
        _ = OpenAsync(context);
    }

    private async Task OpenAsync(AppContext context)
    {
        try
        {
            var session = context.Services.GetService<IAuthSession>();
            var diagnostics = context.Services.GetService<NetworkDiagnosticsService>();
            var sessionIds = Array.Empty<string>();

            if (session is { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens })
            {
                try
                {
                    var options = new SignalRTransportOptions(
                        url.TrimEnd('/') + "/hubs/terminals",
                        new TerminalSessionDimensions(80, 24, 800, 480),
                        tokenProvider: () => session.Tokens?.AccessToken,
                        accessToken: tokens.AccessToken,
                        diagnostics: diagnostics);
                    sessionIds = (await TerminalHubConnection.ListSessionsAsync(options))
                        .Where(x => !x.HasExited && !TerminalViewModel.IsSessionOpen(x.SessionId))
                        .OrderBy(x => x.CreatedAt)
                        .Select(x => x.SessionId)
                        .ToArray();
                }
                catch { /* an unavailable server falls back to a normal new terminal window */ }
            }

            // No restorable process means this activation starts one fresh terminal. If every
            // existing process is already represented by a window, this is also the explicit
            // way to open an additional terminal from the desktop.
            if (sessionIds.Length == 0)
                OpenWindow(context, session, diagnostics, null);
            else
                foreach (var sessionId in sessionIds)
                    if (TerminalViewModel.TryReserveSession(sessionId))
                        OpenWindow(context, session, diagnostics, sessionId);
        }
        finally
        {
            Volatile.Write(ref _opening, 0);
        }
    }

    private void OpenWindow(AppContext context, IAuthSession? session, NetworkDiagnosticsService? diagnostics, string? sessionId)
    {
        var settingsClient = context.Services.GetRequiredService<ITerminalSettingsClient>();
        var viewModel = new TerminalViewModel(session, settingsClient, diagnostics, sessionId);
        var view = new TerminalView
        {
            DataContext = viewModel,
        };
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.terminal.display_name"), view,
            bounds: new Rect(120, 80, 820, 540),
            iconGlyph: Manifest.IconGlyph);
        viewModel.RequestSettingsAsync = async () =>
        {
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("terminal.settings"), dialog =>
            {
                viewModel.CloseSettingsAction = () => dialog.Close(true);
                return new TerminalSettingsView { DataContext = viewModel };
            }, new Size(460, 330));
        };
    }
}
