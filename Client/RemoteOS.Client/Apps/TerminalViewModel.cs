using System.Collections.Concurrent;
using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Hubs;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;

namespace Client.Apps;

/// <summary>
/// 终端应用 View-Model。管理 RoyalTerminal <see cref="TerminalControl"/> 与远端持久会话的附加/切换/恢复。
/// </summary>
/// <remarks>
/// <para><b>断开语义</b>（远端模式）：</para>
/// <list type="bullet">
/// <item>关闭终端窗口（<see cref="Detach"/>，且 <see cref="IAuthSession.State"/> 仍 Authenticated）→ <see cref="SignalRTransportFactory.KillActiveAsync"/> 杀 PTY。</item>
/// <item>桌面关闭/登出（State 已 Unauthenticated）或网络掉线 → <see cref="SignalRTransportFactory.StopActiveAsync"/> 仅关连接，PTY 存活供再次登录恢复。</item>
/// <item>"断开"按钮 → 显式 <see cref="SignalRTransportFactory.KillActiveAsync"/>。</item>
/// </list>
/// <para><b>恢复</b>：打开终端时拉取该用户的会话列表，自动附加最近一个本进程未占用的存活会话（服务端回放缓冲快照重现历史）。</para>
/// <para><b>多实例</b>：进程内 <see cref="_openSessions"/> 记录已开 sessionId，避免两个窗口附加同一会话。</para>
/// </remarks>
public partial class TerminalViewModel : ObservableObject
{
    private const int Columns = 120;
    private const int Rows = 32;

    /// <summary>本进程已打开的远端会话 ID（防止多窗附加同一会话；进程退出即丢失）。</summary>
    private static readonly ConcurrentDictionary<string, byte> _openSessions = new();

    private TerminalControl? _terminal;
    private SignalRTransportFactory? _transportFactory;
    private readonly IAuthSession? _session;

    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private bool _hasExited;
    [ObservableProperty] private TerminalSessionInfo[] _sessions = Array.Empty<TerminalSessionInfo>();
    [ObservableProperty] private TerminalSessionInfo? _selectedSession;

    public TerminalViewModel(IAuthSession? session = null) => _session = session;

    /// <summary>控件 Loaded 时由 View 调用。订阅事件并启动首个会话（自动恢复或新建）。</summary>
    public async Task AttachAsync(TerminalControl terminal, SignalRTransportFactory transportFactory)
    {
        _terminal = terminal;
        _transportFactory = transportFactory;
        _terminal.ProcessExited += OnProcessExited;
        _terminal.TitleChanged += OnTitleChanged;

        await StartSessionAsync(initial: true);
    }

    /// <summary>控件离开视觉树（窗口关闭）时调用。按断开语义决定杀/留 PTY。</summary>
    public void Detach()
    {
        if (_terminal is null)
            return;

        _terminal.ProcessExited -= OnProcessExited;
        _terminal.TitleChanged -= OnTitleChanged;

        // 远端模式且桌面仍认证中 → 用户只关了终端窗口 → 手动终止；否则（桌面登出/掉线）→ 保留 PTY。
        var kill = _session is { State: AuthSessionState.Authenticated };

        ReleaseActiveSession(kill);

        try { _terminal.StopPty(); } catch { /* best effort */ }

        _terminal = null;
        _transportFactory = null;
    }

    /// <summary>启动一个会话。initial=true 时自动挑选恢复目标，否则按指定 sessionId 附加（null=新建）。</summary>
    private async Task StartSessionAsync(bool initial, string? sessionId = null)
    {
        if (_terminal is null)
            return;

        HasExited = false;
        Status = "启动中…";

        var dimensions = new TerminalSessionDimensions(Columns, Rows, WidthPixels: 1200, HeightPixels: 640);

        ITerminalTransportOptions options;
        string? resumeId = sessionId;

        if (_session is { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } })
        {
            Status = "连接远端终端…";

            // 每次启动前刷新会话列表（ComboBox 始终最新）
            var listOpts = BuildRemoteOptions(url, dimensions, sessionId: null);
            try
            {
                var list = await TerminalHubConnection.ListSessionsAsync(listOpts);
                Sessions = list.ToArray();
            }
            catch
            {
                Sessions = Array.Empty<TerminalSessionInfo>();
            }

            if (initial && resumeId is null)
            {
                // 自动恢复：最近一个存活且本进程未占用的会话
                resumeId = Sessions
                    .Where(s => !s.HasExited && !_openSessions.ContainsKey(s.SessionId))
                    .OrderByDescending(s => s.CreatedAt)
                    .FirstOrDefault()?.SessionId;
            }

            options = BuildRemoteOptions(url, dimensions, resumeId);
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
            // 记录本进程占用并选中
            if (_transportFactory?.CurrentSessionId is string id)
            {
                _openSessions.TryAdd(id, 0);
                SelectedSession = Sessions.FirstOrDefault(s => s.SessionId == id);
                if (SelectedSession is null && Sessions.Length > 0)
                {
                    // 新建会话不在旧列表里：补一条占位，便于切换回其它会话
                    var extended = Sessions.Append(new TerminalSessionInfo(id, DateTimeOffset.UtcNow, false)).ToArray();
                    Sessions = extended;
                    SelectedSession = extended.First(s => s.SessionId == id);
                }
            }
            Status = "已连接";
        }
        catch (Exception ex)
        {
            HasExited = true;
            Status = $"启动失败：{ex.Message}";
        }
    }

    /// <summary>释放当前活动会话（kill=true 杀 PTY；false 仅关连接保留 PTY），并从本进程占用集合移除。</summary>
    private void ReleaseActiveSession(bool kill)
    {
        if (_transportFactory is null)
            return;
        var sid = _transportFactory.CurrentSessionId;
        if (sid is not null)
            _openSessions.TryRemove(sid, out _);
        _ = kill ? _transportFactory.KillActiveAsync() : _transportFactory.StopActiveAsync();
    }

    private SignalRTransportOptions BuildRemoteOptions(string serverUrl, TerminalSessionDimensions dims, string? sessionId)
    {
        var url = serverUrl.TrimEnd('/') + "/hubs/terminals";
        return new SignalRTransportOptions(
            hubUrl: url,
            dimensions: dims,
            tokenProvider: () => _session?.Tokens?.AccessToken,
            accessToken: _session?.Tokens?.AccessToken,
            sessionId: sessionId);
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
        ReleaseActiveSession(kill: true); // 杀旧起新
        try { _terminal.StopPty(); } catch { /* ignore */ }
        await StartSessionAsync(initial: false, sessionId: null);
    }

    [RelayCommand]
    private void Clear()
    {
        _terminal?.ClearScrollback();
    }

    /// <summary>断开当前会话：显式杀 PTY 并从服务端移除。</summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        ReleaseActiveSession(kill: true);
        try { _terminal?.StopPty(); } catch { /* ignore */ }
        HasExited = true;
        Status = "已断开";
        // 从本地列表移除已被杀的会话
        var cur = SelectedSession?.SessionId;
        if (cur is not null)
            Sessions = Sessions.Where(s => s.SessionId != cur).ToArray();
        SelectedSession = Sessions.FirstOrDefault();
    }

    /// <summary>新建终端会话（保留当前会话不动，仅切换视图到新会话）。</summary>
    [RelayCommand]
    private async Task NewTerminalAsync()
    {
        if (_terminal is null)
            return;
        ReleaseActiveSession(kill: false); // 不杀旧会话，供后续切换回来
        try { _terminal.StopPty(); } catch { /* ignore */ }
        await StartSessionAsync(initial: false, sessionId: null);
    }

    /// <summary>切换到下拉框选中的会话（保留当前会话不动）。</summary>
    [RelayCommand]
    private async Task SwitchSessionAsync(TerminalSessionInfo? info)
    {
        if (_terminal is null || info is null)
            return;
        if (_transportFactory?.CurrentSessionId == info.SessionId)
            return; // 已是该会话
        ReleaseActiveSession(kill: false); // 不杀旧会话
        try { _terminal.StopPty(); } catch { /* ignore */ }
        await StartSessionAsync(initial: false, sessionId: info.SessionId);
    }
}
