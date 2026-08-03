using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;

namespace Client.Apps;

/// <summary>
/// View-model for the built-in Terminal application. Owns the RoyalTerminal
/// <see cref="TerminalControl"/> reference once the view is loaded and starts a <b>remote</b> PTY
/// session via a SignalR transport (Server-side PTY, JWT-authenticated). Falls back to a local
/// PTY when no authenticated session is available (dev convenience). The view calls
/// <see cref="AttachAsync"/> on Loaded and <see cref="Detach"/> when removed from the visual tree.
/// </summary>
/// <remarks>
/// The <see cref="SignalRTransportFactory"/> is created by <see cref="TerminalView"/> and injected into
/// the <c>TerminalControl</c> via its 9-parameter constructor (the <c>TerminalTransportFactory</c> property
/// is read-only). The view passes the same factory instance here so we can stop the active transport on
/// detach/restart.
/// </remarks>
public partial class TerminalViewModel : ObservableObject
{
    private const int Columns = 120;
    private const int Rows = 32;

    private TerminalControl? _terminal;
    private SignalRTransportFactory? _transportFactory;
    private readonly IAuthSession? _session;

    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private bool _hasExited;

    public TerminalViewModel(IAuthSession? session = null) => _session = session;

    /// <summary>
    /// Called by the view once the <see cref="TerminalControl"/> is loaded. The
    /// <paramref name="transportFactory"/> is the same instance injected into the control's constructor.
    /// </summary>
    public async Task AttachAsync(TerminalControl terminal, SignalRTransportFactory transportFactory)
    {
        _terminal = terminal;
        _transportFactory = transportFactory;
        _terminal.ProcessExited += OnProcessExited;
        _terminal.TitleChanged += OnTitleChanged;

        await StartSessionAsync();
    }

    /// <summary>Called by the view when it leaves the visual tree (window closed) — stops the session.</summary>
    public void Detach()
    {
        if (_terminal is null)
            return;

        _terminal.ProcessExited -= OnProcessExited;
        _terminal.TitleChanged -= OnTitleChanged;

        // Stop the SignalR transport first (closes connection → server OnDisconnectedAsync disposes PTY),
        // then ask the control to stop any local PTY as a fallback.
        if (_transportFactory is not null)
            _ = _transportFactory.StopActiveAsync();
        try { _terminal.StopPty(); } catch { /* best effort */ }

        _terminal = null;
        _transportFactory = null;
    }

    private async Task StartSessionAsync()
    {
        if (_terminal is null)
            return;

        HasExited = false;
        Status = "启动中…";

        var dimensions = new TerminalSessionDimensions(Columns, Rows, WidthPixels: 1200, HeightPixels: 640);

        // Remote Mode (default): authenticated session → Server PTY over SignalR.
        // Local fallback: no session (dev) → local PTY via the inner composite factory.
        ITerminalTransportOptions options;
        if (_session is { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens })
        {
            Status = "连接远端终端…";
            options = new SignalRTransportOptions(
                hubUrl: $"{url.TrimEnd('/')}/hubs/terminals",
                dimensions: dimensions,
                tokenProvider: () => _session.Tokens?.AccessToken,
                accessToken: tokens.AccessToken);
        }
        else
        {
            Status = "本地终端（未登录）…";
            options = new PtyTransportOptions(
                Command: null,
                WorkingDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment: null,
                Dimensions: dimensions);
        }

        try
        {
            await _terminal.StartSessionAsync(options, CancellationToken.None);
            Status = "已连接";
        }
        catch (Exception ex)
        {
            HasExited = true;
            Status = $"启动失败：{ex.Message}";
        }
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        HasExited = true;
        Status = exitCode == 0 ? "进程已退出" : $"进程已退出（退出码 {exitCode}）";
    }

    private void OnTitleChanged(object? sender, string title)
    {
        // Shell OSC 0/2 title updates arrive here (parsed client-side by the VT processor).
        if (!string.IsNullOrWhiteSpace(title))
            Status = title;
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (_terminal is null)
            return;
        try { _terminal.StopPty(); } catch { /* ignore */ }
        if (_transportFactory is not null)
            await _transportFactory.StopActiveAsync();
        await StartSessionAsync();
    }

    [RelayCommand]
    private void Clear()
    {
        _terminal?.ClearScrollback();
    }
}
