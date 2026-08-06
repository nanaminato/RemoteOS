using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Collections.ObjectModel;
using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Identity;

namespace Client.ViewModels.Login;

/// <summary>登录窗口视图模型。参考 Windows mstsc 远程桌面连接工具：服务器地址 + 用户名 + 密码 + 连接。
/// 通过 IAuthSession 发起登录，状态/错误反馈到 UI。设备信息自动采集（本机名/平台/客户端版本）。</summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthSession _session;
    private bool _loadingSavedProfiles;

    public LoginViewModel(IAuthSession session)
    {
        _session = session;
        SavedProfiles = new ObservableCollection<SavedLoginProfile>();
    }

    public ObservableCollection<SavedLoginProfile> SavedProfiles { get; }

    // 输入与连接状态变化时，自动通知 ConnectCommand 重新评估 CanExecute。
    // 此前缺少通知，导致填写完账号密码后按钮仍处于禁用状态（无法点击）。
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _serverUrl = "http://localhost:5090";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _username = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    // Debug 和生产版本都默认启用；用户可在共享设备上取消勾选。
    private bool _rememberServer = true;

    [ObservableProperty]
    private bool _rememberPassword = true;

    [ObservableProperty]
    private SavedLoginProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOptionsText))]
    private bool _showOptions = true;

    [ObservableProperty]
    private bool _hasSavedPasswordProfiles;

    public string ShowOptionsText => ShowOptions ? "隐藏选项" : "显示选项";

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _hasError;

    partial void OnServerUrlChanged(string value) => ClearError();
    partial void OnUsernameChanged(string value) => ClearError();
    partial void OnPasswordChanged(string value) => ClearError();
    partial void OnSelectedProfileChanged(SavedLoginProfile? value)
    {
        if (value is not null && !_loadingSavedProfiles)
            _ = SelectSavedProfileAsync(value);
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        HasError = false;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken ct)
    {
        IsConnecting = true;
        StatusMessage = "正在连接…";
        ClearError();

        try
        {
            var request = new LoginRequest(
                Username, Password,
                ClientPlatform: DetectClientPlatform(),
                DeviceName: Environment.MachineName,
                ClientVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
            await _session.LoginAsync(ServerUrl, request, RememberServer, RememberPassword, ct);
            StatusMessage = "连接成功，正在进入桌面…";
        }
        catch (RemoteOsAuthException ex)
        {
            ErrorMessage = MapProblemToMessage(ex);
            HasError = true;
            StatusMessage = string.Empty;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = MapHttpError(ex);
            HasError = true;
            StatusMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = string.Empty;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanConnect()
        => !IsConnecting
           && !string.IsNullOrWhiteSpace(ServerUrl)
           && !string.IsNullOrWhiteSpace(Username)
           && !string.IsNullOrWhiteSpace(Password);

    public async Task LoadSavedProfilesAsync(CancellationToken ct = default)
    {
        _loadingSavedProfiles = true;
        try
        {
            var profiles = await _session.GetSavedProfilesAsync(ct);
            SavedProfiles.Clear();
            foreach (var profile in profiles)
                SavedProfiles.Add(profile);
            HasSavedPasswordProfiles = profiles.Any(profile => profile.HasPassword);
            ShowOptions = !HasSavedPasswordProfiles;
        }
        finally
        {
            _loadingSavedProfiles = false;
        }
    }

    private async Task SelectSavedProfileAsync(SavedLoginProfile profile)
    {
        ServerUrl = profile.ServerUrl;
        Username = profile.Username;
        Password = profile.Password ?? string.Empty;
        RememberServer = true;
        RememberPassword = profile.HasPassword;

        // In compact mode, choosing a password-bearing entry is the one-action login path.
        // When options are already visible, selection only fills the form so it can be reviewed or edited.
        if (ShowOptions || !profile.HasPassword || IsConnecting)
            return;

        IsConnecting = true;
        StatusMessage = "正在使用已保存的凭据连接…";
        ClearError();
        try
        {
            var connected = await _session.TryLoginSavedAsync(profile.ServerUrl, profile.Username);
            if (connected)
            {
                StatusMessage = "连接成功，正在进入桌面…";
                return;
            }

            Password = string.Empty;
            RememberPassword = false;
            ErrorMessage = "已保存的密码不可用，请重新输入密码。";
            HasError = true;
            StatusMessage = string.Empty;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void RevealOptions()
        => ShowOptions = true;

    /// <summary>运行时探测客户端宿主平台，而非硬编码。PlatformKind 目前仅 Linux/Windows。</summary>
    private static PlatformKind DetectClientPlatform()
        => OperatingSystem.IsWindows() ? PlatformKind.Windows : PlatformKind.Linux;

    /// <summary>HttpRequestException → 可操作的 UI 文案。重点区分连接拒绝/重置/超时，
    /// 这些通常对应服务器未启动、地址端口不对，或 HTTP/HTTPS 协议不匹配（最易踩坑）。</summary>
    private static string MapHttpError(HttpRequestException ex)
    {
        if (Walk<SocketException>(ex) is { } sock)
        {
            return sock.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    "无法连接到服务器：连接被拒绝。请确认服务器已启动，且地址与端口正确（开发期默认 http://localhost:5090）。",
                SocketError.ConnectionReset =>
                    "无法连接到服务器：连接被远程主机强制关闭。请确认服务器已启动，且客户端与服务器协议一致（HTTP 与 HTTPS 不可混用）。",
                SocketError.TimedOut =>
                    "连接服务器超时，请检查网络或服务器地址。",
                _ => $"无法连接到服务器：{sock.Message}",
            };
        }
        return $"无法连接到服务器：{ex.Message}";
    }

    /// <summary>沿 InnerException 链查找首个指定类型的异常（HttpRequestException 常包裹多层）。</summary>
    private static T? Walk<T>(Exception? ex) where T : Exception
    {
        while (ex is not null)
        {
            if (ex is T t) return t;
            ex = ex.InnerException;
        }
        return null;
    }

    /// <summary>ProblemDetails.Type → 本地化 UI 文案。错误码见 RemoteOS.Login.md 错误处理矩阵。</summary>
    private static string MapProblemToMessage(RemoteOsAuthException ex) => ex.Type switch
    {
        "https://remoteos.app/problems/invalid-credential"  => "用户名或密码错误",
        "https://remoteos.app/problems/account-locked"      => "账户已锁定，请联系管理员",
        "https://remoteos.app/problems/account-disabled"    => "账户已禁用",
        "https://remoteos.app/problems/password-expired"    => "密码已过期，请先在服务器上修改",
        "https://remoteos.app/problems/account-expired"     => "账户已过期",
        "https://remoteos.app/problems/account-restriction" => "账户登录受限",
        "https://remoteos.app/problems/invalid-input"       => "请填写完整信息",
        "https://remoteos.app/problems/auth-failed"         => "登录失败，请稍后重试",
        _ => string.IsNullOrEmpty(ex.Detail) ? "登录失败" : ex.Detail,
    };
}
