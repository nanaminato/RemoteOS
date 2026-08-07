namespace Server.Identity;

/// <summary>Authentication schemes that keep user tokens separate from app capability tokens.</summary>
public static class RemoteOsAuthSchemes
{
    public const string User = "RemoteOS.User";
    public const string FileCapability = "RemoteOS.FileCapability";
    public const string FileCapabilityTokenType = "file_capability";
    public const string TokenTypeClaim = "token_type";
    public const string ScopeClaim = "scope";
}
