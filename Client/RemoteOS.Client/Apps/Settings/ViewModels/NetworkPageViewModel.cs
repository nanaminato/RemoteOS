using System.Diagnostics;
using Client.Services;
using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps.Settings.ViewModels;

/// <summary>「网络」页：只读展示到 Server 的连接状态，并提供「测试连接」测量往返延迟。
/// 远程宿主 OS 的网络配置（网卡/DNS/路由）变更需 sudo/UAC 提权（硬约束），本页不涉及——
/// RemoteOS 客户端只消费到 Server 的 HTTP 连接。</summary>
public sealed partial class NetworkPageViewModel : SettingsPageViewModel
{
    private readonly IAuthSession _session;
    private readonly IRemoteOsClient _remote;

    public NetworkPageViewModel(ShellSettings settings, IAuthSession session, IRemoteOsClient remote, Action? save)
        : base(settings, save)
    {
        _session = session;
        _remote = remote;
    }

    public override string Glyph => "🌐";
    public override string DisplayName => "网络";

    public string ConnectionState => _session.State switch
    {
        AuthSessionState.Authenticated => "已连接",
        AuthSessionState.Connecting => "连接中…",
        _ => "未连接",
    };

    public string ServerUrl => _session.ServerUrl ?? "—";
    public string UserName => _session.CurrentUser?.Username ?? "—";
    public string WorkspaceName => _session.CurrentWorkspace?.Name ?? "—";
    public bool IsConnected => _session.State == AuthSessionState.Authenticated;

    [ObservableProperty] private string _latencyText = "未测试";
    [ObservableProperty] private bool _isTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens })
        {
            LatencyText = "未连接，无法测试";
            return;
        }

        IsTesting = true;
        LatencyText = "测试中…";
        try
        {
            var sw = Stopwatch.StartNew();
            await _remote.GetMeAsync(url, tokens.AccessToken);
            sw.Stop();
            LatencyText = $"{sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            LatencyText = $"失败：{ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest => !IsTesting;
    partial void OnIsTestingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();
}
