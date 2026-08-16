using RemoteOS.Core.Applications;
using RemoteOS.WindowManager;

namespace RemoteOS.AppSDK;

/// <summary>One request to activate a Shell-owned <c>remoteos://</c> route or a manifest-declared external URI.</summary>
public sealed record AppActivationRequest(
    Uri Uri,
    AppId? SourceAppId = null,
    bool UserInitiated = true,
    string? CorrelationId = null);

/// <summary>Stable outcome for a local activation request.</summary>
public enum AppActivationStatus
{
    Activated,
    InvalidUri,
    RouteNotFound,
    Unavailable,
}

public sealed record AppActivationResult(AppActivationStatus Status, AppId? TargetAppId = null)
{
    public bool Succeeded => Status == AppActivationStatus.Activated;
}

/// <summary>
/// Host-owned activation surface. Applications never instantiate or discover another
/// application directly; the Shell validates and resolves the URI.
/// </summary>
public interface IAppActivationService
{
    AppActivationResult Activate(AppActivationRequest request);
}

/// <summary>Host-owned lookup for a user's default application for a URI scheme.</summary>
/// <remarks>
/// Implementations are optional. Without one, the runtime activates a scheme only when exactly
/// one registered application declares that it can handle the URI.
/// </remarks>
public interface IUriSchemeDefaultResolver
{
    AppId? ResolveDefaultApplication(string scheme);
}

/// <summary>
/// Optional built-in application extension for registered <c>remoteos://</c> routes. The handler is also
/// invoked for an already-open single-window application, before that window is focused.
/// </summary>
public interface IAppActivationHandler
{
    bool CanHandleActivation(Uri uri);

    void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow);
}

/// <summary>Source-bound activation surface exposed to an application context.</summary>
public interface IAppActivation
{
    AppActivationResult Activate(Uri uri, bool userInitiated = true, string? correlationId = null);
}

/// <summary>Stable Shell-owned activation URIs exposed to built-in and package applications.</summary>
public static class RemoteOsActivationUris
{
    public static Uri SettingsPersonalization { get; } = new("remoteos://settings/personalization");
    public static Uri SettingsApplications { get; } = new("remoteos://settings/apps");

    public static Uri SettingsAppPermissions(AppId appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId.Value);
        return new Uri($"remoteos://settings/apps/{Uri.EscapeDataString(appId.Value)}/permissions");
    }

    /// <summary>
    /// Internal Explorer file-open route. Third-party applications should request a file
    /// capability instead of constructing host paths.
    /// </summary>
    public static Uri OpenFile(AppId appId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri($"remoteos://file/open?appId={Uri.EscapeDataString(appId.Value)}&path={Uri.EscapeDataString(path)}");
    }
}
