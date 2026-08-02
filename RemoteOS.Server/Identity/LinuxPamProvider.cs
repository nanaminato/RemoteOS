namespace Server.Identity;

/// <summary>Linux PAM 身份认证 Provider（占位）。未来用 libpam 或 PAM 绑定库实现。
/// 当前在非 Windows 平台注册，Verify 返回未实现错误，GetUserInfo 抛 PlatformNotSupportedException。</summary>
public sealed class LinuxPamProvider : IIdentityProvider
{
    public CredentialVerifyResult Verify(string username, string password)
        => CredentialVerifyResult.Failed("Linux PAM 认证尚未实现", CredentialError.Unknown);

    public PlatformUserInfo GetUserInfo(string username)
        => throw new PlatformNotSupportedException("Linux PAM provider not implemented.");
}
