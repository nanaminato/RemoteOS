using Client.Services;
using Client.Services.Auth;
using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Workspace;

namespace Client.Apps.Settings.ViewModels;

/// <summary>「系统」页：只读展示连接与账户信息（版本 / Server URL / 用户 / Workspace / 设备 / 连接状态）。</summary>
public sealed class SystemPageViewModel : SettingsPageViewModel
{
    private readonly IAuthSession _session;

    public SystemPageViewModel(ShellSettings settings, IAuthSession session, Action? save)
        : base(settings, save) => _session = session;

    public override string Glyph => "💻";
    public override string DisplayNameKey => "settings.page.system";
    public override string DisplayName => "System";

    public string AppVersion => "RemoteOS 0.1";
    public string ServerUrl => _session.ServerUrl ?? "未连接";
    public string UserName => _session.CurrentUser?.Username ?? "—";
    public string Platform => _session.CurrentUser?.Platform.ToString() ?? "—";
    public string WorkspaceName => _session.CurrentWorkspace?.Name ?? "—";
    public string DeviceName => _session.CurrentDevice?.Name ?? "—";
    public string DeviceRole => _session.AssignedRole.ToString();
    public string ConnectionState => _session.State switch
    {
        AuthSessionState.Authenticated => "已连接",
        AuthSessionState.Connecting => "连接中…",
        _ => "未连接",
    };
    public string UserCreated => _session.CurrentUser?.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string LastLogin => _session.CurrentUser?.LastLoginAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—";
}
