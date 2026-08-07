using RemoteOS.Core.Applications;

namespace Client.Services.AppPermissions;

/// <summary>Host-owned grant registry. Only the Settings app should mutate grants.</summary>
public interface IAppPermissionManager
{
    bool IsGranted(AppId appId, string permissionId);
    void SetGranted(AppId appId, string permissionId, bool granted);
}
