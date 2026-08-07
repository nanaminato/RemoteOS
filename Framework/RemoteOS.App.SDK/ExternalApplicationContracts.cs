using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;
using Avalonia.Controls;

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

/// <summary>Optional extension for package apps that can open a remote file from RemoteExplorer.</summary>
public interface IExternalFileOpenApplication
{
    Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default);
}

/// <summary>Capability-only context supplied to a third-party package application.</summary>
public interface IExternalAppContext
{
    AppId AppId { get; }
    IAppPermissionScope Permissions { get; }
    IDesktopAppearance DesktopAppearance { get; }
    IServerMonitor ServerMonitor { get; }
    IServerFiles ServerFiles { get; }
    IExternalFileApiAccess FileApi { get; }
    IExternalMediaService Media { get; }
    ISettingsNavigation Settings { get; }
    IExternalAppWindowService Windows { get; }
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

/// <summary>Allows an application to bring the user to a relevant, host-owned Settings page.</summary>
public interface ISettingsNavigation
{
    /// <summary>Opens Settings and selects its Applications page.</summary>
    Task OpenApplicationsAsync();
}

/// <summary>Window creation surface for package applications. Every window is owned by the package app id.</summary>
public interface IExternalAppWindowService
{
    IExternalAppWindowHandle ShowWindow(
        string title,
        Control content,
        Rect? bounds = null,
        string? iconGlyph = null,
        bool canResize = true,
        bool canMinimize = true,
        bool canMaximize = true);
}

/// <summary>Window handle with a token cancelled as soon as its managed window closes.</summary>
public interface IExternalAppWindowHandle
{
    ManagedWindow Window { get; }
    CancellationToken Closed { get; }
    bool IsFullScreen { get; }
    void EnterFullScreen();
    void ExitFullScreen();
}

/// <summary>Result returned by a host-mediated capability call.</summary>
public enum AppCapabilityResult
{
    Succeeded,
    PermissionDenied,
    InvalidArgument,
    Unavailable,
}
