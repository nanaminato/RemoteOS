using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Tunnels;

public enum TunnelProtocol { Tcp, Udp, Http, Https }
public enum TunnelAuthKind { None, Token }
public enum TunnelTlsMode { Default, Disable, Force }
public enum TunnelRuntimeMode { Managed, External }
public enum TunnelConnectionState { SavedNotApplied, Starting, Connected, Disconnected, RuntimeUnavailable, Unknown }
public enum TunnelRuntimeState { NotInstalled, Available, Running, Stopped, ExternalInvalid, Unknown }

/// <summary>Safe profile projection. Authentication material is intentionally represented only by state.</summary>
public sealed record TunnelServerProfileDto(
    Guid Id, string Name, string Host, int Port, TunnelAuthKind AuthKind, bool TokenConfigured,
    TunnelTlsMode TlsMode, TunnelRuntimeMode RuntimeMode, string? ExternalExecutablePath,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Safe desired-state projection. It never contains generated TOML or credentials.</summary>
public sealed record TunnelDefinitionDto(
    Guid Id, Guid ServerProfileId, string Name, string ProviderId, TunnelProtocol Protocol,
    string LocalHost, int LocalPort, int? RemotePort, string? Domain, bool Enabled,
    bool Encryption, bool Compression, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    TunnelConnectionState State = TunnelConnectionState.SavedNotApplied, string ProblemCode = "");

public sealed record TunnelRuntimeDto(
    string RuntimeId, TunnelRuntimeMode Mode, TunnelRuntimeState State, string? Version,
    string? ExecutablePath, string ProblemCode = "", DateTimeOffset? StartedAt = null,
    string? PreviousVersion = null, bool IntegrityVerified = false);

public sealed record TunnelOperationResultDto(bool Succeeded, TunnelConnectionState State, string ProblemCode = "");
public sealed record TunnelLogEntryDto(DateTimeOffset Timestamp, string Level, string Message);

public sealed record UpsertTunnelServerProfileRequest(
    string Name, string Host, int Port, TunnelAuthKind AuthKind, TunnelTlsMode TlsMode,
    TunnelRuntimeMode RuntimeMode, string? ExternalExecutablePath, long? ExpectedRevision = null);

public sealed record SetTunnelProfileTokenRequest(string Token);

public sealed record UpsertTunnelDefinitionRequest(
    Guid ServerProfileId, string Name, TunnelProtocol Protocol, string LocalHost, int LocalPort,
    int? RemotePort, string? Domain, bool Enabled, bool Encryption, bool Compression,
    long? ExpectedRevision = null);

/// <summary>Explicit external-runtime detection request. It only inspects the specified absolute executable path.</summary>
public sealed record DetectExternalTunnelRuntimeRequest(string ExecutablePath);
public sealed record InstallManagedTunnelRuntimeRequest(bool Confirmed, string Version);
