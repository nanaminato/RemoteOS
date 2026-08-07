using RemoteOS.Core.Applications;

namespace RemoteOS.AppSDK;

/// <summary>
/// Entry point implemented by a package application. Unlike <see cref="IRemoteApplication"/>,
/// it deliberately receives no service provider or host implementation objects.
/// </summary>
public interface IExternalRemoteApplication
{
    ApplicationManifest Manifest { get; }

    Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default);
}

/// <summary>Capability-only context supplied to a third-party package application.</summary>
public interface IExternalAppContext
{
    AppId AppId { get; }
    IAppPermissionScope Permissions { get; }
    IDesktopAppearance DesktopAppearance { get; }
}

/// <summary>Read-only view of the grants the current application has received from the user.</summary>
public interface IAppPermissionScope
{
    bool IsGranted(string permissionId);
}

/// <summary>Host-mediated desktop appearance operations available to package applications.</summary>
public interface IDesktopAppearance
{
    Task<AppCapabilityResult> SetWallpaperAsync(string wallpaperKey, CancellationToken cancellationToken = default);
}

/// <summary>Result returned by a host-mediated capability call.</summary>
public enum AppCapabilityResult
{
    Succeeded,
    PermissionDenied,
    InvalidArgument,
    Unavailable,
}
