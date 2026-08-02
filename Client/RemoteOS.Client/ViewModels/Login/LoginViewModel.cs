using System.Net.Http;
using System.Reflection;
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

    public LoginViewModel(IAuthSession session) => _session = session;

    [ObservableProperty] private string _serverUrl = "http://localhost:5090";
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _hasError;

    partial void OnServerUrlChanged(string value) => ClearError();
    partial void OnUsernameChanged(string value) => ClearError();
    partial void OnPasswordChanged(string value) => ClearError();
    partial void OnIsConnectingChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();

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
                ClientPlatform: PlatformKind.Windows,
                DeviceName: Environment.MachineName,
                ClientVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
            await _session.LoginAsync(ServerUrl, request, ct);
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
            ErrorMessage = $"无法连接到服务器：{ex.Message}";
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
