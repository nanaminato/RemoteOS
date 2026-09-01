using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Proxy;

public enum ProxyOperatingMode { Tun, ListenerOnly, RemoteOSOnly }
public enum ProxyRuntimeMode { None, Managed, External }
public enum ProxyRuntimeState { NotInstalled, Installing, Stopped, Starting, Running, Reloading, Stopping, Updating, Recovering, Degraded, Failed }
public enum ProxyTunState { Disabled, Enabling, Enabled, Disabling, Recovering, Failed }
public enum ProxyHealthState { Unknown, Healthy, Degraded, Failed, RecoveryRequired }
public enum ProxyOperationState { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }
public enum ProxyLifecycleAction { Start, Stop, Restart }

/// <summary>All public Proxy failures use this lower-case dotted contract.</summary>
public static class ProxyProblemCodes
{
    public const string RuntimeNotInstalled = "proxy.runtime_not_installed";
    public const string RuntimeUnsupportedPlatform = "proxy.runtime_unsupported_platform";
    public const string RuntimeVersionUnsupported = "proxy.runtime_version_unsupported";
    public const string RuntimeIntegrityFailed = "proxy.runtime_integrity_failed";
    public const string RuntimeHealthCheckFailed = "proxy.runtime_health_check_failed";
    public const string ExternalRuntimeInvalid = "proxy.external_runtime_invalid";
    public const string ServiceUnavailable = "proxy.service_unavailable";
    public const string PrivilegedOperationUnavailable = "proxy.privileged_operation_unavailable";
    public const string ConfigInvalid = "proxy.config_invalid";
    public const string ConfigApplyFailed = "proxy.config_apply_failed";
    public const string ControllerUnavailable = "proxy.controller_unavailable";
    public const string ControllerAuthenticationFailed = "proxy.controller_authentication_failed";
    public const string ControllerResponseInvalid = "proxy.controller_response_invalid";
    public const string ControllerTimeout = "proxy.controller_timeout";
    public const string ManagementRouteUnsafe = "proxy.management_route_unsafe";
    public const string PlatformCapabilityUnavailable = "proxy.platform_capability_unavailable";
    public const string TunPermissionRequired = "proxy.tun_permission_required";
    public const string TunActivationFailed = "proxy.tun_activation_failed";
    public const string RecoveryRequired = "proxy.recovery_required";
    public const string RecoveryFailed = "proxy.recovery_failed";
    public const string OperationInterrupted = "proxy.operation_interrupted";
    public const string IdempotencyKeyRequired = "proxy.idempotency_key_required";
    public const string PermissionDenied = "proxy.permission_denied";
    public const string NotSupported = "proxy.not_supported";
}

public static class ProxyApiRoutes
{
    public const string Proxy = "/api/v1/proxy";
    public const string Overview = Proxy;
    public const string Runtime = Proxy + "/runtime";
    public const string LifecyclePattern = "/lifecycle/{action}";
    public const string Tun = Proxy + "/tun";
    public const string Profiles = Proxy + "/profiles";
    public const string ProfilePattern = "/profiles/{profileId:guid}";
    public const string ProfileConfigurationPattern = "/profiles/{profileId:guid}/configuration";
    public const string Groups = Proxy + "/groups";
    public const string GroupSelectionPattern = "/groups/{groupName}/selection";
    public const string Connections = Proxy + "/connections";
    public const string ConnectionPattern = "/connections/{connectionId}";
    public const string Logs = Proxy + "/logs";
    public const string Dns = Proxy + "/dns";
    public const string Settings = Proxy + "/settings";
    public const string Recovery = Proxy + "/recovery";
    public const string OperationsPattern = "/operations/{operationId:guid}";
    public const string RuntimeInstall = Runtime + "/install";
    public const string RuntimeInstallFromFile = Runtime + "/install/from-file";
    public const string RuntimeRollback = Runtime + "/rollback";
    public const string RuntimeUninstall = Runtime + "/uninstall";
    public const string RuntimeExternalDetection = Runtime + "/detect-external";
    public const string Lifecycle = Proxy + "/lifecycle/{action}";
    public const string TunEnable = Tun + "/enable";
    public const string TunDisable = Tun + "/disable";
    public const string TunEmergencyDisable = Tun + "/emergency-disable";
    public const string ProfileActivatePattern = "/profiles/{profileId:guid}/activate";
    public const string ProfileConfigurationApplyPattern = "/profiles/{profileId:guid}/configuration/apply";
}

public sealed record ProxyEngineCapabilities(
    [property: JsonPropertyName("supportsConfigurationValidation")] bool SupportsConfigurationValidation,
    [property: JsonPropertyName("supportsReload")] bool SupportsReload,
    [property: JsonPropertyName("supportsGroups")] bool SupportsGroups,
    [property: JsonPropertyName("supportsConnections")] bool SupportsConnections,
    [property: JsonPropertyName("supportsBoundedLogs")] bool SupportsBoundedLogs,
    [property: JsonPropertyName("supportsDnsStatus")] bool SupportsDnsStatus);

public sealed record ProxyPlatformCapabilities(
    [property: JsonPropertyName("supportsTun")] bool SupportsTun,
    [property: JsonPropertyName("supportsAutoRoute")] bool SupportsAutoRoute,
    [property: JsonPropertyName("supportsAutoRedirect")] bool SupportsAutoRedirect,
    [property: JsonPropertyName("supportsDnsHijack")] bool SupportsDnsHijack,
    [property: JsonPropertyName("supportsNamedPipeController")] bool SupportsNamedPipeController,
    [property: JsonPropertyName("supportsUnixSocketController")] bool SupportsUnixSocketController,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

public sealed record ProxyHealthDto(
    [property: JsonPropertyName("runtimeState")] ProxyRuntimeState RuntimeState,
    [property: JsonPropertyName("tunState")] ProxyTunState TunState,
    [property: JsonPropertyName("state")] ProxyHealthState State,
    [property: JsonPropertyName("controllerReachable")] bool ControllerReachable,
    [property: JsonPropertyName("networkReachable")] bool NetworkReachable,
    [property: JsonPropertyName("managementRouteSafe")] bool ManagementRouteSafe,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

public sealed record ProxyRuntimeDto(
    [property: JsonPropertyName("engineId")] string EngineId,
    [property: JsonPropertyName("mode")] ProxyRuntimeMode Mode,
    [property: JsonPropertyName("state")] ProxyRuntimeState State,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("previousVersion")] string? PreviousVersion,
    [property: JsonPropertyName("integrityVerified")] bool IntegrityVerified,
    [property: JsonPropertyName("externalPathConfigured")] bool ExternalPathConfigured,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

public sealed record ProxyProfileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("engineId")] string EngineId,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed record ProxyGroupDto(string Name, string Type, string? Selected, IReadOnlyList<string> Proxies);
public sealed record ProxyConnectionDto(string Id, string Network, string Source, string Destination, string Rule, string Chains, DateTimeOffset StartedAt);
public sealed record ProxyLogEntryDto(DateTimeOffset Timestamp, string Level, string Message);
public sealed record ProxyDnsStatusDto(bool Enabled, bool HijackEnabled, string? Mode, string ProblemCode = "");
public sealed record ProxySettingsDto(bool SystemProxyEnabled, bool AllowLan, bool DnsEnabled, bool Ipv6Enabled, bool UnifiedDelay,
    string LogLevel, int MixedPort);
public sealed record ProxyRecoveryStatusDto(bool RecoveryRequired, bool HasRecoveryMarker, DateTimeOffset? MarkerCreatedAt, string ProblemCode = "");
public sealed record ProxyOperationDto(Guid OperationId, string Kind, ProxyOperationState State, string Stage, string ProblemCode, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

public sealed record ProxyOverviewDto(string EngineId, ProxyEngineCapabilities EngineCapabilities, ProxyPlatformCapabilities PlatformCapabilities,
    ProxyRuntimeDto Runtime, ProxyHealthDto Health, ProxyOperatingMode OperatingMode, ProxyProfileDto? ActiveProfile,
    int ActiveConnections, ProxyRecoveryStatusDto Recovery);

public sealed record UpsertProxyProfileRequest(string Name, string EngineId, long? ExpectedRevision = null);
public sealed record SelectProxyGroupRequest(string Proxy);
public sealed record ProxyTunRequest(Guid ProfileId);
public sealed record ProxyRuntimeRequest(string EngineId, string? Version = null, string? ExternalPath = null);
/// <summary>Installs a pinned Mihomo archive already present on the RemoteOS Server.</summary>
public sealed record InstallProxyRuntimeFromFileRequest(string EngineId, string? Version, string ArchivePath);
public sealed record ProxyLifecycleRequest(bool Confirmed = false);
public sealed record ApplyProxyConfigurationRequest(string Yaml);
public sealed record UpdateProxySettingsRequest(bool SystemProxyEnabled, bool AllowLan, bool DnsEnabled, bool Ipv6Enabled, bool UnifiedDelay,
    string LogLevel, int MixedPort);
public sealed record ProxyOperationAcceptedDto(Guid OperationId);
