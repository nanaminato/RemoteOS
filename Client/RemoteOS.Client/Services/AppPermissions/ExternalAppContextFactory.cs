using Client.Apps.Settings;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;
using CoreAppPermissions = RemoteOS.Core.Applications.AppPermissions;

namespace Client.Services.AppPermissions;

/// <summary>Creates the capability-only context used by the future package loader.</summary>
public sealed class ExternalAppContextFactory
{
    private readonly IAppPermissionManager _permissions;
    private readonly ShellSettings _settings;
    private readonly ISettingsClient _settingsClient;
    private readonly IAuthSession _session;
    private readonly DefaultAppRegistry _defaultApps;
    private readonly IWindowManager _windowManager;

    public ExternalAppContextFactory(
        IAppPermissionManager permissions,
        ShellSettings settings,
        ISettingsClient settingsClient,
        IAuthSession session,
        DefaultAppRegistry defaultApps,
        IWindowManager windowManager)
    {
        _permissions = permissions;
        _settings = settings;
        _settingsClient = settingsClient;
        _session = session;
        _defaultApps = defaultApps;
        _windowManager = windowManager;
    }

    public IExternalAppContext Create(AppId appId) => new ExternalAppContext(
        appId,
        new AppPermissionScope(appId, _permissions),
        new DesktopAppearanceCapability(appId, _permissions, _settings, _settingsClient, _session, _defaultApps),
        new ExternalAppWindowService(appId, _windowManager));

    private sealed record ExternalAppContext(
        AppId AppId,
        IAppPermissionScope Permissions,
        IDesktopAppearance DesktopAppearance,
        IExternalAppWindowService Windows) : IExternalAppContext;

    private sealed class ExternalAppWindowService(AppId appId, IWindowManager windowManager) : IExternalAppWindowService
    {
        public ManagedWindow ShowWindow(
            string title,
            Avalonia.Controls.Control content,
            Rect? bounds = null,
            string? iconGlyph = null,
            bool canResize = true,
            bool canMinimize = true,
            bool canMaximize = true)
            => windowManager.Create(new WindowCreateOptions(
                OwnerAppId: appId,
                Title: title,
                Content: content,
                Bounds: bounds,
                IconGlyph: iconGlyph,
                CanResize: canResize,
                CanMinimize: canMinimize,
                CanMaximize: canMaximize));
    }

    private sealed class AppPermissionScope(AppId appId, IAppPermissionManager permissions) : IAppPermissionScope
    {
        public bool IsGranted(string permissionId) => permissions.IsGranted(appId, permissionId);
    }

    private sealed class DesktopAppearanceCapability(
        AppId appId,
        IAppPermissionManager permissions,
        ShellSettings settings,
        ISettingsClient settingsClient,
        IAuthSession session,
        DefaultAppRegistry defaultApps) : IDesktopAppearance
    {
        public async Task<AppCapabilityResult> SetWallpaperAsync(string wallpaperKey, CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.DesktopWallpaperWrite))
                return AppCapabilityResult.PermissionDenied;

            if (!settings.TrySetWallpaperKey(wallpaperKey))
                return AppCapabilityResult.InvalidArgument;

            if (session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
                return AppCapabilityResult.Succeeded;

            try
            {
                await settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id,
                    settings.ToPreferences(defaultApps.Snapshot), cancellationToken);
                return AppCapabilityResult.Succeeded;
            }
            catch
            {
                return AppCapabilityResult.Unavailable;
            }
        }
    }
}
