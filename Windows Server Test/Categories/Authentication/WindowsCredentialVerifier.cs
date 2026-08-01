using System.Runtime.InteropServices;

namespace Windows_Server_Tester.Categories.Authentication;

/// <summary>
/// 使用 Win32 LogonUser API 验证 Windows 账号密码是否正确。
/// 支持本地账户与域账户，自动解析 user / domain\user / user@domain 三种格式。
/// </summary>
public static class WindowsCredentialVerifier
{
    public static CredentialVerifyResult Verify(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return CredentialVerifyResult.Failed("用户名不能为空", CredentialError.InvalidInput);

        ParseUserName(userName, out var user, out var domain);
        domain ??= Environment.MachineName;   // 纯用户名 → 默认验证本机

        IntPtr token = IntPtr.Zero;
        try
        {
            bool ok = LogonUser(
                user, domain, password,
                LOGON32_LOGON_NETWORK,      // 3：轻量登录，不加载用户配置
                LOGON32_PROVIDER_DEFAULT,   // 0
                out token);

            if (ok)
                return CredentialVerifyResult.Ok(domain, user);

            int err = Marshal.GetLastWin32Error();
            return err switch
            {
                ERROR_LOGON_FAILURE          => CredentialVerifyResult.Failed("用户名或密码错误", CredentialError.BadCredentials, err),
                ERROR_NO_SUCH_USER           => CredentialVerifyResult.Failed("用户不存在", CredentialError.NoSuchUser, err),
                ERROR_ACCOUNT_DISABLED       => CredentialVerifyResult.Failed("账户已禁用", CredentialError.AccountDisabled, err),
                ERROR_ACCOUNT_LOCKED_OUT     => CredentialVerifyResult.Failed("账户已锁定", CredentialError.AccountLockedOut, err),
                ERROR_PASSWORD_EXPIRED       => CredentialVerifyResult.Failed("密码已过期", CredentialError.PasswordExpired, err),
                ERROR_ACCOUNT_EXPIRED        => CredentialVerifyResult.Failed("账户已过期", CredentialError.AccountExpired, err),
                ERROR_ACCOUNT_RESTRICTION    => CredentialVerifyResult.Failed("账户受限（如空密码策略）", CredentialError.AccountRestriction, err),
                ERROR_INVALID_LOGON_HOURS    => CredentialVerifyResult.Failed("不在允许登录的时间段", CredentialError.AccountRestriction, err),
                ERROR_LOGON_TYPE_NOT_GRANTED => CredentialVerifyResult.Failed("未授予“从网络访问此计算机”权限", CredentialError.AccountRestriction, err),
                _                            => CredentialVerifyResult.Failed($"登录失败，Win32 错误码 {err}", CredentialError.Unknown, err),
            };
        }
        catch (Exception ex)
        {
            return CredentialVerifyResult.FromError("调用 LogonUser 异常", ex.Message);
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static void ParseUserName(string raw, out string user, out string? domain)
    {
        // user@domain
        if (raw.IndexOf('@') is int at and >= 0)
        {
            user = raw[..at];
            domain = raw[(at + 1)..];
            return;
        }

        // domain\user
        if (raw.IndexOf('\\') is int bs and >= 0)
        {
            domain = raw[..bs];
            user = raw[(bs + 1)..];
            return;
        }

        // 纯 user → domain 留空，由调用方补本机名
        user = raw;
        domain = null;
    }

    // ---- 常量 ----
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCOUNT_RESTRICTION = 1327;
    private const int ERROR_INVALID_LOGON_HOURS = 1328;
    private const int ERROR_PASSWORD_EXPIRED = 1330;
    private const int ERROR_ACCOUNT_DISABLED = 1331;
    private const int ERROR_NO_SUCH_USER = 1317;
    private const int ERROR_ACCOUNT_EXPIRED = 1793;
    private const int ERROR_ACCOUNT_LOCKED_OUT = 1909;
    private const int ERROR_LOGON_TYPE_NOT_GRANTED = 1385;

    // ---- P/Invoke ----
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string lpszUsername, string lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

public enum CredentialError
{
    None,
    InvalidInput,
    BadCredentials,
    NoSuchUser,
    AccountDisabled,
    AccountLockedOut,
    PasswordExpired,
    AccountExpired,
    AccountRestriction,
    Unknown,
}

public sealed record CredentialVerifyResult(
    bool Success,
    string Message,
    CredentialError Error,
    int? Win32ErrorCode)
{
    public static CredentialVerifyResult Ok(string domain, string user)
        => new(true, $"验证通过：{domain}\\{user}", CredentialError.None, null);

    public static CredentialVerifyResult Failed(string msg, CredentialError err, int? win32 = null)
        => new(false, msg, err, win32);

    public static CredentialVerifyResult FromError(string msg, string detail)
        => new(false, $"{msg}：{detail}", CredentialError.Unknown, null);
}
