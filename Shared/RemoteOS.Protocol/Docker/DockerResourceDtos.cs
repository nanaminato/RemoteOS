namespace RemoteOS.Protocol.Docker;

public sealed record DockerContainerDto(string Id, string Names, string Image, string State, string Status);
public sealed record DockerImageDto(string Id, string Repository, string Tag, string Size, string CreatedSince);
public sealed record DockerNetworkDto(string Id, string Name, string Driver, string Scope);
public sealed record DockerVolumeDto(string Name, string Driver, string Mountpoint);
public sealed record DockerNetworkDetailsDto(string Id, string Name, string Driver, string Scope, IReadOnlyList<string> Containers);
public sealed record DockerVolumeDetailsDto(string Name, string Driver, string Mountpoint, IReadOnlyDictionary<string, string> Labels);

/// <summary>Structured container lifecycle request. Confirmation is required for irreversible actions.</summary>
public sealed record DockerContainerActionRequest(bool Force = false, bool Confirmed = false);

/// <summary>Stable result that does not expose Docker daemon error text to clients.</summary>
public sealed record DockerOperationResult(bool Success, string ProblemCode);
public sealed record DockerImageOperationRequest(string ImageReference, bool Confirmed = false);
public sealed record DockerContainerCreateRequest(string Name, string Image, IReadOnlyList<string> Arguments);
public sealed record DockerNetworkCreateRequest(string Name, string Driver = "bridge", bool Confirmed = false);
public sealed record DockerVolumeCreateRequest(string Name, string Driver = "local", bool Confirmed = false);
public sealed record DockerContainerLogsDto(IReadOnlyList<string> Lines, bool Truncated);
public sealed record DockerContainerStatsDto(string ContainerId, string CpuPercent, string MemoryUsage, string NetworkIo, string BlockIo);
public sealed record DockerBuildRequest(string ContextDirectory, string ImageReference, string? Dockerfile = null);
/// <summary>Bounded base64 archive transfer. The server never accepts arbitrary host paths.</summary>
public sealed record DockerImageArchiveDto(string ImageReference, string ContentBase64);
