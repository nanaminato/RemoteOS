using RemoteOS.Core.Applications;

namespace Client.Services.AppPermissions;

/// <summary>Host-owned grant registry. Only the Settings app should mutate grants.</summary>
public interface IAppPermissionManager
{
    event EventHandler<AppPermissionChangedEventArgs>? Changed;
    bool IsGranted(AppId appId, string permissionId);
    void SetGranted(AppId appId, string permissionId, bool granted);
}

public sealed class AppPermissionChangedEventArgs(AppId appId, string permissionId, bool isGranted) : EventArgs
{
    public AppId AppId { get; } = appId;
    public string PermissionId { get; } = permissionId;
    public bool IsGranted { get; } = isGranted;
}
